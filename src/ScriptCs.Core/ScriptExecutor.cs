﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.XPath;
using Common.Logging;
using ScriptCs.Contracts;

namespace ScriptCs
{
    public class ScriptExecutor : IScriptExecutor
    {
        public static readonly string[] DefaultReferences = new[] { "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Xml", "System.Xml.Linq", "System.Net.Http", typeof(ScriptExecutor).Assembly.Location, typeof(IScriptEnvironment).Assembly.Location };
        public static readonly string[] DefaultNamespaces = new[] { "System", "System.Collections.Generic", "System.Linq", "System.Text", "System.Threading.Tasks", "System.IO", "System.Net.Http" };
        private const string ScriptLibrariesInjected = "ScriptLibrariesInjected";

        public IFileSystem FileSystem { get; private set; }

        public IFilePreProcessor FilePreProcessor { get; private set; }

        public IScriptEngine ScriptEngine { get; private set; }

        public ILog Logger { get; private set; }

        public AssemblyReferences References { get; private set; }

        public ICollection<string> Namespaces { get; private set; }

        public ScriptPackSession ScriptPackSession { get; protected set; }

        public IScriptLibraryComposer ScriptLibraryComposer { get; protected set; }
 
        public ScriptExecutor(IFileSystem fileSystem, IFilePreProcessor filePreProcessor, IScriptEngine scriptEngine, ILog logger, IScriptLibraryComposer composer)
        {
            Guard.AgainstNullArgument("fileSystem", fileSystem);
            Guard.AgainstNullArgumentProperty("fileSystem", "BinFolder", fileSystem.BinFolder);
            Guard.AgainstNullArgumentProperty("fileSystem", "DllCacheFolder", fileSystem.DllCacheFolder);
            References = new AssemblyReferences(DefaultReferences);
            Namespaces = new Collection<string>();
            ImportNamespaces(DefaultNamespaces);
            FileSystem = fileSystem;
            FilePreProcessor = filePreProcessor;
            ScriptEngine = scriptEngine;
            Logger = logger;
            ScriptLibraryComposer = composer;
        }

        public void ImportNamespaces(params string[] namespaces)
        {
            Guard.AgainstNullArgument("namespaces", namespaces);

            foreach (var @namespace in namespaces)
            {
                Namespaces.Add(@namespace);
            }
        }

        public void AddReferences(params Assembly[] assemblies)
        {
            Guard.AgainstNullArgument("assemblies", assemblies);

            foreach (var assembly in assemblies
                .Where(assembly =>
                    assembly != typeof(ScriptExecutor).Assembly && assembly != typeof(IScriptEnvironment).Assembly))
            {
                References.Assemblies.Add(assembly);
            }
        }

        public void RemoveReferences(params Assembly[] assemblies)
        {
            Guard.AgainstNullArgument("assemblies", assemblies);

            foreach (var assembly in assemblies)
            {
                References.Assemblies.Remove(assembly);
            }
        }

        public void AddReferences(params string[] paths)
        {
            Guard.AgainstNullArgument("paths", paths);

            foreach (var path in paths
                .Where(path =>
                    !string.Equals(
                        Path.GetFileName(path),
                        Path.GetFileName(typeof(ScriptExecutor).Assembly.Location),
                        StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(
                        Path.GetFileName(path),
                        Path.GetFileName(typeof(IScriptEnvironment).Assembly.Location),
                        StringComparison.OrdinalIgnoreCase)))
            {
                References.PathReferences.Add(path);
            }
        }

        public void RemoveReferences(params string[] paths)
        {
            Guard.AgainstNullArgument("paths", paths);

            foreach (var path in paths)
            {
                References.PathReferences.Remove(path);
            }
        }

        public void RemoveNamespaces(params string[] namespaces)
        {
            Guard.AgainstNullArgument("namespaces", namespaces);

            foreach (var @namespace in namespaces)
            {
                Namespaces.Remove(@namespace);
            }
        }

        public virtual void Initialize(IEnumerable<string> paths, IEnumerable<IScriptPack> scriptPacks, params string[] scriptArgs)
        {
            AddReferences(paths.ToArray());
            var bin = Path.Combine(FileSystem.CurrentDirectory, FileSystem.BinFolder);
            var cache = Path.Combine(FileSystem.CurrentDirectory, FileSystem.DllCacheFolder);

            ScriptEngine.BaseDirectory = bin;
            ScriptEngine.CacheDirectory = cache;

            Logger.Debug("Initializing script packs");
            var scriptPackSession = new ScriptPackSession(scriptPacks, scriptArgs);
            ScriptPackSession = scriptPackSession;
            scriptPackSession.InitializePacks();
        }

        public virtual void Reset()
        {
            References = new AssemblyReferences(DefaultReferences);
            Namespaces.Clear();
            ImportNamespaces(DefaultNamespaces);

            ScriptPackSession.State.Clear();
        }

        public virtual void Terminate()
        {
            Logger.Debug("Terminating packs");
            ScriptPackSession.TerminatePacks();
        }

        public virtual ScriptResult Execute(string script, params string[] scriptArgs)
        {
            var path = Path.IsPathRooted(script) ? script : Path.Combine(FileSystem.CurrentDirectory, script);
            var result = FilePreProcessor.ProcessFile(path);
            References.PathReferences.UnionWith(result.References);
            var namespaces = Namespaces.Union(result.Namespaces);
            ScriptEngine.FileName = Path.GetFileName(path);

            Logger.Debug("Starting execution in engine");

            InjectScriptLibraries(Path.GetDirectoryName(path), result, ScriptPackSession.State);
            //AddContractsIfNotPresent(References.PathReferences);
            return ScriptEngine.Execute(result.Code, scriptArgs, References, namespaces, ScriptPackSession);
        }
        
        /*
        protected internal void AddContractsIfNotPresent(HashSet<string> references )
        {
            var contracts = references.SingleOrDefault(p=>p.EndsWith("ScriptCs.Contracts.dll", StringComparison.InvariantCultureIgnoreCase));
            if (contracts != null)
            {
                references.Remove(contracts);
            }
            references.Add(typeof(IScriptEnvironment).Assembly.Location);
        }
         */

        public virtual ScriptResult ExecuteScript(string script, params string[] scriptArgs)
        {
            var result = FilePreProcessor.ProcessScript(script);
            References.PathReferences.UnionWith(result.References);
            var namespaces = Namespaces.Union(result.Namespaces);

            Logger.Debug("Starting execution in engine");
            
            InjectScriptLibraries(FileSystem.CurrentDirectory, result, ScriptPackSession.State);
            return ScriptEngine.Execute(result.Code, scriptArgs, References, namespaces, ScriptPackSession);
        }

        protected internal virtual void InjectScriptLibraries(
            string workingDirectory, 
            FilePreProcessorResult result,
            IDictionary<string, object> state 
        )
        {
            if (state.ContainsKey(ScriptLibrariesInjected))
            {
                return;
            }

            var scriptLibrariesPreProcessorResult = LoadScriptLibraries(workingDirectory);

            if (scriptLibrariesPreProcessorResult != null)
            {
                result.Code = scriptLibrariesPreProcessorResult.Code + Environment.NewLine + result.Code;
                result.References.AddRange(scriptLibrariesPreProcessorResult.References);
                result.Namespaces.AddRange(scriptLibrariesPreProcessorResult.Namespaces);
            }
            state.Add(ScriptLibrariesInjected, null);
        }

        protected internal virtual FilePreProcessorResult LoadScriptLibraries(string workingDirectory)
        {
            var scriptLibrariesPath = Path.Combine(workingDirectory, FileSystem.PackagesFolder,
                ScriptLibraryComposer.ScriptLibrariesFile);

            if (FileSystem.FileExists(scriptLibrariesPath))
            {
                Logger.DebugFormat("Found Script Library at {0}", scriptLibrariesPath);
                return FilePreProcessor.ProcessFile(scriptLibrariesPath);
            }

            return null;
        }
    }
}