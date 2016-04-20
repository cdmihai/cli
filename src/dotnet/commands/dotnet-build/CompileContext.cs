// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Dotnet.Cli.Compiler.Common;
using Microsoft.DotNet.Cli.Compiler.Common;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.DotNet.ProjectModel;
using Microsoft.DotNet.ProjectModel.Utilities;
using Microsoft.DotNet.Tools.Compiler;
using Microsoft.Extensions.PlatformAbstractions;
using Microsoft.DotNet.ProjectModel.Compilation;

namespace Microsoft.DotNet.Tools.Build
{
    // todo: Convert CompileContext into a DAG of dependencies: if a node needs recompilation, the entire path up to root needs compilation
    // Knows how to orchestrate compilation for a ProjectContext
    // Collects icnremental safety checks and transitively compiles a project context
    internal class CompileContext
    {
        public static readonly string[] KnownCompilers = { "csc", "vbc", "fsc" };

        private readonly ProjectContext _rootProject;
        private readonly ProjectDependenciesFacade _rootProjectDependencies;
        private readonly BuilderCommandApp _args;
        private readonly IncrementalPreconditions _preconditions;
        private IncrementalManager _incrementalManager;

        public bool IsSafeForIncrementalCompilation => !_preconditions.PreconditionsDetected();

        public CompileContext(ProjectContext rootProject, BuilderCommandApp args)
        {
            _rootProject = rootProject;

            // Cleaner to clone the args and mutate the clone than have separate CompileContext fields for mutated args
            // and then reasoning which ones to get from args and which ones from fields.
            _args = (BuilderCommandApp)args.ShallowCopy();

            // Set up dependencies
            _rootProjectDependencies = new ProjectDependenciesFacade(_rootProject, _args.ConfigValue);

            // gather preconditions
            _preconditions = GatherIncrementalPreconditions();

            _incrementalManager = new IncrementalManager(_rootProject, _args);
        }

        public bool Compile(bool incremental)
        {
            CreateOutputDirectories();

            return CompileDependencies(incremental) && CompileRootProject(incremental);
        }

        private bool CompileRootProject(bool incremental)
        {
            try
            {
                if (incremental && !NeedsRebuild(_rootProject, _rootProjectDependencies))
                {
                    return true;
                }

                var success = InvokeCompileOnRootProject();

                PrintSummary(success);

                return success;
            }
            finally
            {
                StampProjectWithSDKVersion(_rootProject);
            }
        }

        private bool CompileDependencies(bool incremental)
        {
            if (_args.ShouldSkipDependencies)
            {
                return true;
            }

            foreach (var dependency in Sort(_rootProjectDependencies))
            {
                var dependencyProjectContext = ProjectContext.Create(dependency.Path, dependency.Framework, new[] { _rootProject.RuntimeIdentifier });

                try
                {
                    if (incremental && !NeedsRebuild(dependencyProjectContext, new ProjectDependenciesFacade(dependencyProjectContext, _args.ConfigValue)))
                    {
                        continue;
                    }

                    if (!InvokeCompileOnDependency(dependency))
                    {
                        return false;
                    }
                }
                finally
                {
                    StampProjectWithSDKVersion(dependencyProjectContext);
                }
            }

            return true;
        }

        private bool NeedsRebuild(ProjectContext project, ProjectDependenciesFacade projectDependenciesFacade)
        {
            var result = _incrementalManager.NeedsRebuilding(project, projectDependenciesFacade);

            PrintIncrementalResult(project, result);

            return result.NeedsRebuild;
        }

        private void PrintIncrementalResult(ProjectContext project, IncrementalResult result)
        {
            if (result.NeedsRebuild)
            {
                Reporter.Output.WriteLine($"Project {project.GetDisplayName()} will be compiled because {result.Reason}");
                foreach (var item in result.Items)
                {
                    Reporter.Verbose.WriteLine($"\t{item}");
                }
            }
            else
            {
                Reporter.Output.WriteLine(
                    $"Project {project.GetDisplayName()} was previously compiled. Skipping compilation.");
            }
        }

        private void StampProjectWithSDKVersion(ProjectContext project)
        {
            if (File.Exists(DotnetFiles.VersionFile))
            {
                var projectVersionFile = project.GetSDKVersionFile(_args.ConfigValue, _args.BuildBasePathValue, _args.OutputValue);
                var parentDirectory = Path.GetDirectoryName(projectVersionFile);

                if (!Directory.Exists(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                string content = DotnetFiles.ReadAndInterpretVersionFile();

                File.WriteAllText(projectVersionFile, content);
            }
            else
            {
                Reporter.Verbose.WriteLine($"Project {project.GetDisplayName()} was not stamped with a CLI version because the version file does not exist: {DotnetFiles.VersionFile}");
            }
        }

        private void PrintSummary(bool success)
        {
            // todo: Ideally it's the builder's responsibility for adding the time elapsed. That way we avoid cross cutting display concerns between compile and build for printing time elapsed
            if (success)
            {
                Reporter.Output.Write(" " + _preconditions.LogMessage());
                Reporter.Output.WriteLine();
            }

            Reporter.Output.WriteLine();
        }

        private void CreateOutputDirectories()
        {
            if (!string.IsNullOrEmpty(_args.OutputValue))
            {
                Directory.CreateDirectory(_args.OutputValue);
            }
            if (!string.IsNullOrEmpty(_args.BuildBasePathValue))
            {
                Directory.CreateDirectory(_args.BuildBasePathValue);
            }
        }

        private IncrementalPreconditions GatherIncrementalPreconditions()
        {
            var preconditions = new IncrementalPreconditions(_args.ShouldPrintIncrementalPreconditions);

            if (_args.ShouldNotUseIncrementality)
            {
                preconditions.AddForceUnsafePrecondition();
            }

            var projectsToCheck = GetProjectsToCheck();

            foreach (var project in projectsToCheck)
            {
                CollectScriptPreconditions(project, preconditions);
                CollectCompilerNamePreconditions(project, preconditions);
                CollectCheckPathProbingPreconditions(project, preconditions);
            }

            return preconditions;
        }

        // check the entire project tree that needs to be compiled, duplicated for each framework
        private List<ProjectContext> GetProjectsToCheck()
        {
            if (_args.ShouldSkipDependencies)
            {
                return new List<ProjectContext>(1) { _rootProject };
            }

            // include initial root project
            var contextsToCheck = new List<ProjectContext>(1 + _rootProjectDependencies.ProjectDependenciesWithSources.Count) { _rootProject };

            // convert ProjectDescription to ProjectContext
            var dependencyContexts = _rootProjectDependencies.ProjectDependenciesWithSources.Select
                (keyValuePair => ProjectContext.Create(keyValuePair.Value.Path, keyValuePair.Value.Framework));

            contextsToCheck.AddRange(dependencyContexts);


            return contextsToCheck;
        }

        private void CollectCheckPathProbingPreconditions(ProjectContext project, IncrementalPreconditions preconditions)
        {
            var pathCommands = CompilerUtil.GetCommandsInvokedByCompile(project)
                .Select(commandName => Command.CreateDotNet(commandName, Enumerable.Empty<string>(), project.TargetFramework))
                .Where(c => c.ResolutionStrategy.Equals(CommandResolutionStrategy.Path));

            foreach (var pathCommand in pathCommands)
            {
                preconditions.AddPathProbingPrecondition(project.ProjectName(), pathCommand.CommandName);
            }
        }

        private void CollectCompilerNamePreconditions(ProjectContext project, IncrementalPreconditions preconditions)
        {
            if (project.ProjectFile != null)
            {
                var projectCompiler = project.ProjectFile.CompilerName;

                if (!KnownCompilers.Any(knownCompiler => knownCompiler.Equals(projectCompiler, StringComparison.Ordinal)))
                {
                    preconditions.AddUnknownCompilerPrecondition(project.ProjectName(), projectCompiler);
                }
            }
        }

        private void CollectScriptPreconditions(ProjectContext project, IncrementalPreconditions preconditions)
        {
            if (project.ProjectFile != null)
            {
                var preCompileScripts = project.ProjectFile.Scripts.GetOrEmpty(ScriptNames.PreCompile);
                var postCompileScripts = project.ProjectFile.Scripts.GetOrEmpty(ScriptNames.PostCompile);

                if (preCompileScripts.Any())
                {
                    preconditions.AddPrePostScriptPrecondition(project.ProjectName(), ScriptNames.PreCompile);
                }

                if (postCompileScripts.Any())
                {
                    preconditions.AddPrePostScriptPrecondition(project.ProjectName(), ScriptNames.PostCompile);
                }
            }
        }

        private bool InvokeCompileOnDependency(ProjectDescription projectDependency)
        {
            var args = new List<string>();

            args.Add("--framework");
            args.Add($"{projectDependency.Framework}");

            args.Add("--configuration");
            args.Add(_args.ConfigValue);
            args.Add(projectDependency.Project.ProjectDirectory);

            if (!string.IsNullOrWhiteSpace(_args.RuntimeValue))
            {
                args.Add("--runtime");
                args.Add(_args.RuntimeValue);
            }

            if (!string.IsNullOrEmpty(_args.VersionSuffixValue))
            {
                args.Add("--version-suffix");
                args.Add(_args.VersionSuffixValue);
            }

            if (!string.IsNullOrWhiteSpace(_args.BuildBasePathValue))
            {
                args.Add("--build-base-path");
                args.Add(_args.BuildBasePathValue);
            }

            var compileResult = CompileCommand.Run(args.ToArray());

            return compileResult == 0;
        }

        private bool InvokeCompileOnRootProject()
        {
            // todo: add methods to CompilerCommandApp to generate the arg string?
            var args = new List<string>();
            args.Add("--framework");
            args.Add(_rootProject.TargetFramework.ToString());
            args.Add("--configuration");
            args.Add(_args.ConfigValue);

            if (!string.IsNullOrWhiteSpace(_args.RuntimeValue))
            {
                args.Add("--runtime");
                args.Add(_args.RuntimeValue);
            }

            if (!string.IsNullOrEmpty(_args.OutputValue))
            {
                args.Add("--output");
                args.Add(_args.OutputValue);
            }

            if (!string.IsNullOrEmpty(_args.VersionSuffixValue))
            {
                args.Add("--version-suffix");
                args.Add(_args.VersionSuffixValue);
            }

            if (!string.IsNullOrEmpty(_args.BuildBasePathValue))
            {
                args.Add("--build-base-path");
                args.Add(_args.BuildBasePathValue);
            }

            //native args
            if (_args.IsNativeValue)
            {
                args.Add("--native");
            }

            if (_args.IsCppModeValue)
            {
                args.Add("--cpp");
            }

            if (!string.IsNullOrWhiteSpace(_args.CppCompilerFlagsValue))
            {
                args.Add("--cppcompilerflags");
                args.Add(_args.CppCompilerFlagsValue);
            }

            if (!string.IsNullOrWhiteSpace(_args.ArchValue))
            {
                args.Add("--arch");
                args.Add(_args.ArchValue);
            }

            foreach (var ilcArg in _args.IlcArgsValue)
            {
                args.Add("--ilcarg");
                args.Add(ilcArg);
            }

            if (!string.IsNullOrWhiteSpace(_args.IlcPathValue))
            {
                args.Add("--ilcpath");
                args.Add(_args.IlcPathValue);
            }

            if (!string.IsNullOrWhiteSpace(_args.IlcSdkPathValue))
            {
                args.Add("--ilcsdkpath");
                args.Add(_args.IlcSdkPathValue);
            }

            args.Add(_rootProject.ProjectDirectory);

            var compileResult = CompileCommand.Run(args.ToArray());

            var succeeded = compileResult == 0;

            if (succeeded)
            {
                MakeRunnable();
            }

            return succeeded;
        }

        private void CopyCompilationOutput(OutputPaths outputPaths)
        {
            var dest = outputPaths.RuntimeOutputPath;
            var source = outputPaths.CompilationOutputPath;

            // No need to copy if dest and source are the same
            if(string.Equals(dest, source, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var file in outputPaths.CompilationFiles.All())
            {
                var destFileName = file.Replace(source, dest);
                var directoryName = Path.GetDirectoryName(destFileName);
                if (!Directory.Exists(directoryName))
                {
                    Directory.CreateDirectory(directoryName);
                }
                File.Copy(file, destFileName, true);
            }
        }

        private void MakeRunnable()
        {
            var runtimeContext = _rootProject.ProjectFile.HasRuntimeOutput(_args.ConfigValue) ?
                _rootProject.CreateRuntimeContext(_args.GetRuntimes()) :
                _rootProject;

            var outputPaths = runtimeContext.GetOutputPaths(_args.ConfigValue, _args.BuildBasePathValue, _args.OutputValue);
            var libraryExporter = runtimeContext.CreateExporter(_args.ConfigValue, _args.BuildBasePathValue);

            CopyCompilationOutput(outputPaths);

            var executable = new Executable(runtimeContext, outputPaths, libraryExporter, _args.ConfigValue);
            executable.MakeCompilationOutputRunnable();
        }

        private static IEnumerable<ProjectDescription> Sort(ProjectDependenciesFacade dependencies)
        {
            var outputs = new List<ProjectDescription>();

            foreach (var pair in dependencies.Dependencies)
            {
                Sort(pair.Value, dependencies, outputs);
            }

            return outputs.Distinct(new ProjectComparer());
        }

        private static void Sort(LibraryExport node, ProjectDependenciesFacade dependencies, IList<ProjectDescription> outputs)
        {
            // Sorts projects in dependency order so that we only build them once per chain
            ProjectDescription projectDependency;
            foreach (var dependency in node.Library.Dependencies)
            {
                // Sort the children
                Sort(dependencies.Dependencies[dependency.Name], dependencies, outputs);
            }

            // Add this node to the list if it is a project
            if (dependencies.ProjectDependenciesWithSources.TryGetValue(node.Library.Identity.Name, out projectDependency))
            {
                // Add to the list of projects to build
                outputs.Add(projectDependency);
            }
        }

        private class ProjectComparer : IEqualityComparer<ProjectDescription>
        {
            public bool Equals(ProjectDescription x, ProjectDescription y) => string.Equals(x.Identity.Name, y.Identity.Name, StringComparison.Ordinal);
            public int GetHashCode(ProjectDescription obj) => obj.Identity.Name.GetHashCode();
        }
    }
}
