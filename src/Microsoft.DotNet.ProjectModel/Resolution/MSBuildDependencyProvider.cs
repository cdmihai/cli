// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.DotNet.ProjectModel.Graph;
using NuGet.Frameworks;

namespace Microsoft.DotNet.ProjectModel.Resolution
{
    public class MSBuildDependencyProvider
    {
        private readonly FrameworkReferenceResolver _frameworkReferenceResolver;

        public MSBuildDependencyProvider(FrameworkReferenceResolver frameworkReferenceResolver)
        {
            _frameworkReferenceResolver = frameworkReferenceResolver;
        }

        public MSBuildProjectDescription GetDescription(NuGetFramework targetFramework, LockFileProjectLibrary projectLibrary, LockFileTargetLibrary targetLibrary)
        {
            var compatible = targetLibrary.FrameworkAssemblies.Any() ||
                             targetLibrary.CompileTimeAssemblies.Any() ||
                             targetLibrary.RuntimeAssemblies.Any();

            var dependencies = new List<LibraryRange>(targetLibrary.Dependencies.Count + targetLibrary.FrameworkAssemblies.Count);
            PopulateDependencies(dependencies, targetLibrary, targetFramework);

            var path = Path.GetDirectoryName(Path.GetFullPath(projectLibrary.MSBuildProject));
            var exists = Directory.Exists(path);

            if (exists)
            {
                // If the package's compile time assemblies is for a portable profile then, read the assembly metadata
                // and turn System.* references into reference assembly dependencies
                PopulateLegacyPortableDependencies(targetFramework, dependencies, path, targetLibrary);
            }

            var msbuildPackageDescription = new MSBuildProjectDescription(
                path,
                projectLibrary,
                targetLibrary,
                dependencies,
                compatible,
                resolved: compatible && exists);

            return msbuildPackageDescription;
        }

        private void PopulateLegacyPortableDependencies(NuGetFramework targetFramework, List<LibraryRange> dependencies, string packagePath, LockFileTargetLibrary targetLibrary)
        {
            var seen = new HashSet<string>();

            foreach (var assembly in targetLibrary.CompileTimeAssemblies)
            {
                if (IsPlaceholderFile(assembly))
                {
                    continue;
                }

                // (ref/lib)/{tfm}/{assembly}
                var pathParts = assembly.Path.Split(Path.DirectorySeparatorChar);

                if (pathParts.Length != 3)
                {
                    continue;
                }

                var assemblyTargetFramework = NuGetFramework.Parse(pathParts[1]);

                if (!assemblyTargetFramework.IsPCL)
                {
                    continue;
                }

                var assemblyPath = Path.Combine(packagePath, assembly.Path);

                foreach (var dependency in GetDependencies(assemblyPath))
                {
                    if (seen.Add(dependency))
                    {
                        string path;
                        Version version;

                        // If there exists a reference assembly on the requested framework with the same name then turn this into a
                        // framework assembly dependency
                        if (_frameworkReferenceResolver.TryGetAssembly(dependency, targetFramework, out path, out version))
                        {
                            dependencies.Add(new LibraryRange(dependency,
                                LibraryType.ReferenceAssembly,
                                LibraryDependencyType.Build));
                        }
                    }
                }
            }
        }

        private static IEnumerable<string> GetDependencies(string path)
        {
            using (var peReader = new PEReader(File.OpenRead(path)))
            {
                var metadataReader = peReader.GetMetadataReader();

                foreach (var assemblyReferenceHandle in metadataReader.AssemblyReferences)
                {
                    var assemblyReference = metadataReader.GetAssemblyReference(assemblyReferenceHandle);

                    yield return metadataReader.GetString(assemblyReference.Name);
                }
            }
        }

        private void PopulateDependencies(
            List<LibraryRange> dependencies,
            LockFileTargetLibrary targetLibrary,
            NuGetFramework targetFramework)
        {
            foreach (var dependency in targetLibrary.Dependencies)
            {
                dependencies.Add(new LibraryRange(
                    dependency.Id,
                    dependency.VersionRange,
                    LibraryType.Unspecified,
                    LibraryDependencyType.Default));
            }

            if (!targetFramework.IsPackageBased)
            {
                // Only add framework assemblies for non-package based frameworks.
                foreach (var frameworkAssembly in targetLibrary.FrameworkAssemblies)
                {
                    dependencies.Add(new LibraryRange(
                        frameworkAssembly,
                        LibraryType.ReferenceAssembly,
                        LibraryDependencyType.Default));
                }
            }
        }

        public static bool IsPlaceholderFile(string path)
        {
            return string.Equals(Path.GetFileName(path), "_._", StringComparison.Ordinal);
        }

        public static bool IsMSBuildProjectLibrary(LockFileProjectLibrary projectLibrary)
        {
            var hasMSbuildProjectValue = !string.IsNullOrEmpty(projectLibrary.MSBuildProject);
            var doesNotHavePathValue = string.IsNullOrEmpty(projectLibrary.Path);

            return doesNotHavePathValue && hasMSbuildProjectValue;
        }
    }
}
