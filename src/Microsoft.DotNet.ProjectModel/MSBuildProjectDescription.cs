// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.DotNet.ProjectModel.Graph;

namespace Microsoft.DotNet.ProjectModel
{
    /// <summary>
    /// Represents an MSBuild project.
    /// It has been invisibly built by MSBuild, so it behaves like a package: can provide all assets up front
    /// </summary>
    public class MSBuildProjectDescription : PackageDescription
    {
        public MSBuildProjectDescription(
            string path,
            LockFileProjectLibrary projectLibrary,
            LockFileTargetLibrary lockFileLibrary,
            IEnumerable<LibraryRange> dependencies,
            bool compatible,
            bool resolved)
            : base(
                  new LibraryIdentity(projectLibrary.Name, projectLibrary.Version, LibraryType.MSBuildProject),
                  string.Empty, //msbuild projects don't have hashes
                  path,
                  lockFileLibrary,
                  dependencies,
                  resolved: resolved,
                  compatible: compatible,
                  framework: null)
        {
            ProjectLibrary = projectLibrary;
        }

        public LockFileProjectLibrary ProjectLibrary { get; }

        public override IEnumerable<string> GetSharedSources()
        {
            return Enumerable.Empty<string>();
        }

        public override IEnumerable<string> GetAnalyzerReferences()
        {
            return Enumerable.Empty<string>();
        }
    }
}
