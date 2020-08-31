﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Microsoft.CodeAnalysis.Sarif.Visitors
{
    public class InsertOptionalDataVisitor : SarifRewritingVisitor
    {
        private readonly IFileSystem _fileSystem;
        private readonly GitHelper.ProcessRunner _processRunner;

        private Run _run;
        private HashSet<Uri> _repoRootUris;
        private GitHelper _gitHelper;

        private int _ruleIndex;
        private FileRegionsCache _fileRegionsCache;
        private readonly OptionallyEmittedData _dataToInsert;
        private readonly IDictionary<string, ArtifactLocation> _originalUriBaseIds;

        public InsertOptionalDataVisitor(OptionallyEmittedData dataToInsert, Run run)
            : this(dataToInsert, run?.OriginalUriBaseIds)
        {
            _run = run ?? throw new ArgumentNullException(nameof(run));
        }

        public InsertOptionalDataVisitor(
            OptionallyEmittedData dataToInsert,
            IDictionary<string, ArtifactLocation> originalUriBaseIds = null,
            IFileSystem fileSystem = null,
            GitHelper.ProcessRunner processRunner = null)
        {
            _fileSystem = fileSystem ?? new FileSystem();
            _processRunner = processRunner;

            _dataToInsert = dataToInsert;
            _originalUriBaseIds = originalUriBaseIds;
            _ruleIndex = -1;
        }

        public override Run VisitRun(Run node)
        {
            _run = node;
            _gitHelper = new GitHelper(_fileSystem, _processRunner);
            _repoRootUris = new HashSet<Uri>();

            if (_originalUriBaseIds != null)
            {
                _run.OriginalUriBaseIds = _run.OriginalUriBaseIds ?? new Dictionary<string, ArtifactLocation>();

                foreach (string key in _originalUriBaseIds.Keys)
                {
                    _run.OriginalUriBaseIds[key] = _originalUriBaseIds[key];
                }
            }

            if (node == null) { return null; }

            bool scrapeFileReferences = _dataToInsert.HasFlag(OptionallyEmittedData.Hashes) ||
                                        _dataToInsert.HasFlag(OptionallyEmittedData.TextFiles) ||
                                        _dataToInsert.HasFlag(OptionallyEmittedData.BinaryFiles);

            if (scrapeFileReferences)
            {
                var visitor = new AddFileReferencesVisitor();
                visitor.VisitRun(node);
            }

            Run visited = base.VisitRun(node);

            // After all the ArtifactLocations have been visited,
            if (_run.VersionControlProvenance == null && _dataToInsert.HasFlag(OptionallyEmittedData.VersionControlInformation))
            {
                _run.VersionControlProvenance = CreateVersionControlProvenance();
            }

            return visited;
        }

        public override PhysicalLocation VisitPhysicalLocation(PhysicalLocation node)
        {
            if (node.Region == null || node.Region.IsBinaryRegion)
            {
                goto Exit;
            }

            bool insertRegionSnippets = _dataToInsert.HasFlag(OptionallyEmittedData.RegionSnippets);
            bool overwriteExistingData = _dataToInsert.HasFlag(OptionallyEmittedData.OverwriteExistingData);
            bool insertContextCodeSnippets = _dataToInsert.HasFlag(OptionallyEmittedData.ContextRegionSnippets);
            bool populateRegionProperties = _dataToInsert.HasFlag(OptionallyEmittedData.ComprehensiveRegionProperties);

            if (insertRegionSnippets || populateRegionProperties || insertContextCodeSnippets)
            {
                Region expandedRegion;
                ArtifactLocation artifactLocation = node.ArtifactLocation;

                _fileRegionsCache = _fileRegionsCache ?? new FileRegionsCache(_run);

                if (artifactLocation.Uri == null && artifactLocation.Index >= 0)
                {
                    // Uri is not stored at result level, but we have an index to go look in run.Artifacts
                    // we must pick the ArtifactLocation details from run.artifacts array
                    Artifact artifactFromRun = _run.Artifacts[artifactLocation.Index];
                    artifactLocation = artifactFromRun.Location;
                }

                // If we can resolve a file location to a newly constructed
                // absolute URI, we will prefer that
                if (!artifactLocation.TryReconstructAbsoluteUri(_run.OriginalUriBaseIds, out Uri resolvedUri))
                {
                    resolvedUri = artifactLocation.Uri;
                }

                if (!resolvedUri.IsAbsoluteUri) { goto Exit; }

                expandedRegion = _fileRegionsCache.PopulateTextRegionProperties(node.Region, resolvedUri, populateSnippet: insertRegionSnippets);

                ArtifactContent originalSnippet = node.Region.Snippet;

                if (populateRegionProperties)
                {
                    node.Region = expandedRegion;
                }

                if (originalSnippet == null || overwriteExistingData)
                {
                    node.Region.Snippet = expandedRegion.Snippet;
                }
                else
                {
                    node.Region.Snippet = originalSnippet;
                }

                if (insertContextCodeSnippets && (node.ContextRegion == null || overwriteExistingData))
                {
                    node.ContextRegion = _fileRegionsCache.ConstructMultilineContextSnippet(expandedRegion, resolvedUri);
                }
            }

        Exit:
            return base.VisitPhysicalLocation(node);
        }

        public override Artifact VisitArtifact(Artifact node)
        {
            ArtifactLocation fileLocation = node.Location;
            if (fileLocation != null)
            {
                bool workToDo = false;
                bool overwriteExistingData = _dataToInsert.HasFlag(OptionallyEmittedData.OverwriteExistingData);

                workToDo |= (node.Hashes == null || overwriteExistingData) && _dataToInsert.HasFlag(OptionallyEmittedData.Hashes);
                workToDo |= (node.Contents?.Text == null || overwriteExistingData) && _dataToInsert.HasFlag(OptionallyEmittedData.TextFiles);
                workToDo |= (node.Contents?.Binary == null || overwriteExistingData) && _dataToInsert.HasFlag(OptionallyEmittedData.BinaryFiles);

                if (workToDo)
                {
                    if (fileLocation.TryReconstructAbsoluteUri(_run.OriginalUriBaseIds, out Uri uri))
                    {
                        Encoding encoding = null;

                        string encodingText = node.Encoding ?? _run.DefaultEncoding;

                        if (!string.IsNullOrWhiteSpace(encodingText))
                        {
                            try
                            {
                                encoding = Encoding.GetEncoding(encodingText);
                            }
                            catch (ArgumentException) { }
                        }

                        int length = node.Length;
                        node = Artifact.Create(uri, _dataToInsert, encoding: encoding);
                        node.Length = length;
                        fileLocation.Index = -1;
                        node.Location = fileLocation;
                    }
                }
            }

            return base.VisitArtifact(node);
        }

        public override ArtifactLocation VisitArtifactLocation(ArtifactLocation node)
        {
            if (_dataToInsert.HasFlag(OptionallyEmittedData.VersionControlInformation))
            {
                node = ExpressRelativeToRepoRoot(node);
            }

            return base.VisitArtifactLocation(node);
        }

        public override Result VisitResult(Result node)
        {
            _ruleIndex = node.RuleIndex;
            node = base.VisitResult(node);
            _ruleIndex = -1;

            if (string.IsNullOrEmpty(node.Guid) && _dataToInsert.HasFlag(OptionallyEmittedData.Guids))
            {
                node.Guid = Guid.NewGuid().ToString(SarifConstants.GuidFormat);
            }

            return node;
        }

        public override Message VisitMessage(Message node)
        {
            if ((node.Text == null || _dataToInsert.HasFlag(OptionallyEmittedData.OverwriteExistingData)) &&
                _dataToInsert.HasFlag(OptionallyEmittedData.FlattenedMessages))
            {
                MultiformatMessageString formatString = null;
                ReportingDescriptor rule = _ruleIndex != -1 ? _run.Tool.Driver.Rules[_ruleIndex] : null;

                if (rule != null &&
                    rule.MessageStrings != null &&
                    rule.MessageStrings.TryGetValue(node.Id, out formatString))
                {
                    node.Text = node.Arguments?.Count > 0
                        ? rule.Format(node.Id, node.Arguments)
                        : formatString?.Text;
                }

                if (node.Text == null &&
                    _run.Tool.Driver.GlobalMessageStrings?.TryGetValue(node.Id, out formatString) == true)
                {
                    node.Text = node.Arguments?.Count > 0
                        ? string.Format(CultureInfo.CurrentCulture, formatString.Text, node.Arguments.ToArray())
                        : formatString?.Text;
                }
            }
            return base.VisitMessage(node);
        }

        private List<VersionControlDetails> CreateVersionControlProvenance()
        {
            var versionControlProvenance = new List<VersionControlDetails>();

            foreach (Uri repoRootUri in _repoRootUris)
            {
                string repoRootPath = repoRootUri.LocalPath;
                Uri repoRemoteUri = _gitHelper.GetRemoteUri(repoRootPath);
                if (repoRemoteUri != null)
                {
                    versionControlProvenance.Add(
                        new VersionControlDetails
                        {
                            RepositoryUri = repoRemoteUri,
                            RevisionId = _gitHelper.GetCurrentCommit(repoRootPath),
                            Branch = _gitHelper.GetCurrentBranch(repoRootPath),
                            MappedTo = new ArtifactLocation { Uri = repoRootUri }
                        });
                }
            }

            return versionControlProvenance;
        }

        private ArtifactLocation ExpressRelativeToRepoRoot(ArtifactLocation node)
        {
            Uri uri = node.Uri;
            if (uri == null && node.Index >= 0 && _run.Artifacts?.Count > node.Index)
            {
                uri = _run.Artifacts[node.Index].Location.Uri;
            }

            if (uri != null && uri.IsAbsoluteUri && uri.IsFile)
            {
                string repoRootPath = _gitHelper.GetRepositoryRoot(uri.LocalPath);
                if (repoRootPath != null)
                {
                    var repoRootUri = new Uri(repoRootPath, UriKind.Absolute);
                    _repoRootUris.Add(repoRootUri);

                    Uri repoRelativeUri = repoRootUri.MakeRelativeUri(uri);
                    node.Uri = repoRelativeUri;
                    node.UriBaseId = GetUriBaseIdForRepoRoot(repoRootUri);
                }
            }

            return node;
        }

        private string GetUriBaseIdForRepoRoot(Uri repoRootUri)
        {
            // Is there already an entry in OriginalUriBaseIds for this repo?
            if (_run.OriginalUriBaseIds != null)
            {
                foreach (KeyValuePair<string, ArtifactLocation> pair in _run.OriginalUriBaseIds)
                {
                    if (pair.Value.Uri == repoRootUri)
                    {
                        // Yes, so return it.
                        return pair.Key;
                    }
                }
            }

            // No, so add one.
            if (_run.OriginalUriBaseIds == null)
            {
                _run.OriginalUriBaseIds = new Dictionary<string, ArtifactLocation>();
            }

            string uriBaseId = GetNextRepoRootUriBaseId();
            _run.OriginalUriBaseIds.Add(
                uriBaseId,
                new ArtifactLocation
                {
                    Uri = repoRootUri
                });

            return uriBaseId;
        }

        private string GetNextRepoRootUriBaseId()
        {
            ICollection<string> originalUriBaseIdSymbols = _run.OriginalUriBaseIds.Keys;

            for (int i = 0; ; i++)
            {
                string uriBaseId = GetUriBaseId(i);
                if (!originalUriBaseIdSymbols.Contains(uriBaseId))
                {
                    return uriBaseId;
                }
            }
        }

        private const string RepoRootUriBaseIdStem = "REPO_ROOT";

        // When there is only one repo root (the usual case), the uriBaseId is "REPO_ROOT" (unless
        // that symbol is already in use in originalUriBaseIds. The second and subsequent uriBaseIds
        // are REPO_ROOT_2, _3, etc. (again, skipping over any that are in use). We never assign
        // REPO_ROOT_1 (although of course it might exist in originalUriBaseIds).
        internal static string GetUriBaseId(int i)
            => i == 0
            ? RepoRootUriBaseIdStem
            : $"{RepoRootUriBaseIdStem}_{i + 1}";
    }
}
