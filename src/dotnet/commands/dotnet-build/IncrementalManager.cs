// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.DotNet.Cli.Compiler.Common;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.ProjectModel;
using Microsoft.DotNet.ProjectModel.Utilities;
using Microsoft.DotNet.Tools.Compiler;
using Microsoft.Extensions.PlatformAbstractions;
using Microsoft.DotNet.ProjectModel.Compilation;
using NuGet.Protocol.Core.Types;

namespace Microsoft.DotNet.Tools.Build
{
    internal class IncrementalManager
    {
        private readonly ProjectContext _rootProject;
        private readonly BuilderCommandApp _args;

        public IncrementalManager(ProjectContext rootProject, BuilderCommandApp args)
        {
            _rootProject = rootProject;
            _args = args;
        }

        public IncrementalResult NeedsRebuilding(ProjectContext project, ProjectDependenciesFacade dependencies)
        {
            var compilerIO = GetCompileIO(project, dependencies);

            try
            {
                var result = CLIChanged(project);
                if (result.NeedsRebuild)
                {
                    return result;
                }

                result = InputItemsChanged(project, compilerIO);
                if (result.NeedsRebuild)
                {
                    return result;
                }

                result = TimestampsChanged(project, compilerIO);
                if (result.NeedsRebuild)
                {
                    return result;
                }

                return IncrementalResult.DoesNotNeedRebuild;
            }
            finally
            {
                var incrementalCacheFile = project.IncrementalCacheFile(_args.ConfigValue, _args.BuildBasePathValue, _args.OutputValue);
                IncrementalCache.WriteToFile(incrementalCacheFile, new IncrementalCache(compilerIO));
            }
        }

        private IncrementalResult CLIChanged(ProjectContext project)
        {
            var currentVersionFile = DotnetFiles.VersionFile;
            var versionFileFromLastCompile = project.GetSDKVersionFile(_args.ConfigValue, _args.BuildBasePathValue, _args.OutputValue);

            if (!File.Exists(currentVersionFile))
            {
                // this CLI does not have a version file; cannot tell if CLI changed
                return IncrementalResult.DoesNotNeedRebuild;
            }

            if (!File.Exists(versionFileFromLastCompile))
            {
                // this is the first compilation; cannot tell if CLI changed
                return IncrementalResult.DoesNotNeedRebuild;
            }

            var currentContent = DotnetFiles.ReadAndInterpretVersionFile();

            var versionsAreEqual = string.Equals(currentContent, File.ReadAllText(versionFileFromLastCompile), StringComparison.OrdinalIgnoreCase);

            return versionsAreEqual ? IncrementalResult.DoesNotNeedRebuild : new IncrementalResult("the version or bitness of the CLI changed since the last build");
        }

        private IncrementalResult InputItemsChanged(ProjectContext project, CompilerIO compilerIO)
        {
            // check empty inputs / outputs
            if (!compilerIO.Inputs.Any())
            {
                return new IncrementalResult("the project has no inputs");
            }

            if (!compilerIO.Outputs.Any())
            {
                return new IncrementalResult("the project has no outputs");
            }

            // check non existent items
            var result = CheckMissingIO(compilerIO.Inputs, "inputs");
            if (result.NeedsRebuild)
            {
                return result;
            }

            result = CheckMissingIO(compilerIO.Outputs, "outputs");
            if (result.NeedsRebuild)
            {
                return result;
            }

            // check cache against input glob pattern changes
            var incrementalCacheFile = project.IncrementalCacheFile(_args.ConfigValue, _args.BuildBasePathValue, _args.OutputValue);

            if (!File.Exists(incrementalCacheFile))
            {
                // no cache present; cannot tell if anything changed
                return IncrementalResult.DoesNotNeedRebuild;
            }

            var incrementalCache = IncrementalCache.ReadFromFile(incrementalCacheFile);

            var diffResult = compilerIO.DiffInputs(incrementalCache.CompilerIO);

            if (diffResult.Deletions.Any())
            {
                return new IncrementalResult("Input items removed from last build", diffResult.Deletions);
            }

            if (diffResult.Additions.Any())
            {
                return new IncrementalResult("Input items added from last build", diffResult.Deletions);
            }

            return IncrementalResult.DoesNotNeedRebuild;
        }

        private IncrementalResult CheckMissingIO(IEnumerable<string> items, string itemsType)
        {
            var missingItems = items.Where(i => !File.Exists(i)).ToList();

            return missingItems.Any() ? new IncrementalResult($"expected {itemsType} are missing", missingItems) : IncrementalResult.DoesNotNeedRebuild;
        }

        private IncrementalResult TimestampsChanged(ProjectContext project, CompilerIO compilerIO)
        {
            // find the output with the earliest write time
            var minDateUtc = File.GetLastWriteTimeUtc(compilerIO.Outputs.First());

            foreach (var outputPath in compilerIO.Outputs)
            {
                if (File.GetLastWriteTimeUtc(outputPath) >= minDateUtc)
                {
                    continue;
                }

                minDateUtc = File.GetLastWriteTimeUtc(outputPath);
            }

            // find inputs that are older than the earliest output
            var newInputs = compilerIO.Inputs.Where(p => File.GetLastWriteTimeUtc(p) >= minDateUtc);

            return newInputs.Any() ? new IncrementalResult("inputs were modified", newInputs) : IncrementalResult.DoesNotNeedRebuild;
        }

        // computes all the inputs and outputs that would be used in the compilation of a project
        // ensures that all paths are files
        // ensures no missing inputs
        public CompilerIO GetCompileIO(ProjectContext project, ProjectDependenciesFacade dependencies)
        {
            var inputs = new List<string>();
            var outputs = new List<string>();

            var buildConfiguration = _args.ConfigValue;
            var buildBasePath = _args.BuildBasePathValue;
            var outputPath = _args.OutputValue;
            var isRootProject = project == _rootProject;
            
            var calculator = project.GetOutputPaths(buildConfiguration, buildBasePath, outputPath);
            var binariesOutputPath = calculator.CompilationOutputPath;

            // input: project.json
            inputs.Add(project.ProjectFile.ProjectFilePath);

            // input: lock file; find when dependencies change
            AddLockFile(project, inputs);

            // input: source files
            inputs.AddRange(CompilerUtil.GetCompilationSources(project));

            // todo: Factor out dependency resolution between Build and Compile. Ideally Build injects the dependencies into Compile
            // input: dependencies
            AddDependencies(dependencies, inputs);

            var allOutputPath = new HashSet<string>(calculator.CompilationFiles.All());
            if (isRootProject && project.ProjectFile.HasRuntimeOutput(buildConfiguration))
            {
                var runtimeContext = project.CreateRuntimeContext(_args.GetRuntimes());
                foreach (var path in runtimeContext.GetOutputPaths(buildConfiguration, buildBasePath, outputPath).RuntimeFiles.All())
                {
                    allOutputPath.Add(path);
                }
            }

            // output: compiler outputs
            foreach (var path in allOutputPath)
            {
                outputs.Add(path);
            }

            // input compilation options files
            AddCompilationOptions(project, buildConfiguration, inputs);

            // input / output: resources with culture
            AddNonCultureResources(project, calculator.IntermediateOutputDirectoryPath, inputs, outputs);

            // input / output: resources without culture
            AddCultureResources(project, binariesOutputPath, inputs, outputs);

            return new CompilerIO(inputs, outputs);
        }

        private static void AddLockFile(ProjectContext project, List<string> inputs)
        {
            if (project.LockFile == null)
            {
                var errorMessage = $"Project {project.ProjectName()} does not have a lock file.";
                Reporter.Error.WriteLine(errorMessage);
                throw new InvalidOperationException(errorMessage);
            }

            inputs.Add(project.LockFile.LockFilePath);

            if (project.LockFile.ExportFile != null)
            {
                inputs.Add(project.LockFile.ExportFile.ExportFilePath);
            }
        }

        private static void AddDependencies(ProjectDependenciesFacade dependencies, List<string> inputs)
        {
            // add dependency sources that need compilation
            inputs.AddRange(dependencies.ProjectDependenciesWithSources.Values.SelectMany(p => p.Project.Files.SourceFiles));

            // non project dependencies get captured by changes in the lock file
        }

        private static void AddCompilationOptions(ProjectContext project, string config, List<string> inputs)
        {
            var compilerOptions = project.ResolveCompilationOptions(config);

            // input: key file
            if (compilerOptions.KeyFile != null)
            {
                inputs.Add(compilerOptions.KeyFile);
            }
        }

        private static void AddNonCultureResources(ProjectContext project, string intermediaryOutputPath, List<string> inputs, IList<string> outputs)
        {
            foreach (var resourceIO in CompilerUtil.GetNonCultureResources(project.ProjectFile, intermediaryOutputPath))
            {
                inputs.Add(resourceIO.InputFile);

                if (resourceIO.OutputFile != null)
                {
                    outputs.Add(resourceIO.OutputFile);
                }
            }
        }

        private static void AddCultureResources(ProjectContext project, string outputPath, List<string> inputs, List<string> outputs)
        {
            foreach (var cultureResourceIO in CompilerUtil.GetCultureResources(project.ProjectFile, outputPath))
            {
                inputs.AddRange(cultureResourceIO.InputFileToMetadata.Keys);

                if (cultureResourceIO.OutputFile != null)
                {
                    outputs.Add(cultureResourceIO.OutputFile);
                }
            }
        }
    }
}
