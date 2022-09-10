using DotnetAffected.Abstractions;
using LibGit2Sharp;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;

namespace DotnetAffected.Core
{
    /// <summary>
    /// Detects changes using Git.
    /// </summary>
    public class GitChangesProvider : IChangesProvider
    {
        private static readonly Lazy<bool> ResolveMsBuildFileSystemSupported = new Lazy<bool>(() =>
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(typeof(Project).Assembly.Location);
            if (versionInfo.FileMajorPart > 16)
                return true;
            if (versionInfo.FileMajorPart < 16)
                return false;
            return versionInfo.FileMinorPart >= 10;
        });

        /// <summary>
        /// When true, the build system supports virtual filesystem which means nested Directory.Packages.props files
        /// are supported in central package management.
        /// </summary>
        public static bool MsBuildFileSystemSupported => ResolveMsBuildFileSystemSupported.Value;

        /// <inheritdoc />
        public IEnumerable<string> GetChangedFiles(string directory, string from, string to)
        {
            using var repository = new Repository(directory);

            var changes = GetChangesForRange<TreeChanges>(repository, from, to);

            return TreeChangesToPaths(changes, directory);
        }

        public Project? LoadDirectoryPackagePropsProject(string directory, string pathToFile, string? commitRef, bool fallbackToHead)
        {
            var project = LoadProject(directory, pathToFile, commitRef, fallbackToHead);
            if (project is null && MsBuildFileSystemSupported)
            {
                var fi = new FileInfo(pathToFile);
                var parent = fi.Directory?.Parent?.FullName;
                if (parent is not null && parent.Length >= directory.Length)
                    return LoadDirectoryPackagePropsProject(directory, Path.Combine(parent, "Directory.Packages.props"), commitRef, fallbackToHead);
            }

            return project;
        }

        /// <inheritdoc />
        public Project? LoadProject(string directory, string pathToFile, string? commitRef, bool fallbackToHead)
        {
            return MsBuildFileSystemSupported
                ? LoadProjectCore(directory, pathToFile, commitRef, fallbackToHead)
                : LoadProjectLegacy(directory, pathToFile, commitRef, fallbackToHead);
        }

        private Project? LoadProjectCore(string directory, string pathToFile, string? commitRef, bool fallbackToHead)
        {
            Commit? commit;
            
            using var repository = new Repository(directory);
            
            if (string.IsNullOrWhiteSpace(commitRef))
                commit = fallbackToHead ? repository.Head.Tip : null;
            else
                commit = GetCommitOrThrow(repository, commitRef);

            var fs = new MsBuildGitFileSystem(repository, commit);

            if (fs.FileExists(pathToFile))
            {
                var projectCollection = new ProjectCollection();
                
                // Loading using a file path will not work since creating/opening the root project is done directly, no virtual FS there.
                // We must use a reader so we control where the content comes.
                // Later, when we attach it to a Project, imports will be loaded via the git file system...
                using var reader = new XmlTextReader(fs.GetFileStream(pathToFile, FileMode.Open, FileAccess.Read, FileShare.None));
                var projectRootElement = ProjectRootElement.Create(reader, projectCollection);

                // Creating from an XML reader does not have a file system context, i.e. it does not know the root path.
                // It will use the process root path.
                // We need to set the exact path so dynamic imports are resolved relative to the original path.
                // Not that this one must be the absolute path, not relative!
                projectRootElement.FullPath = pathToFile;

                return Project.FromProjectRootElement(projectRootElement, new ProjectOptions
                {
                    LoadSettings = ProjectLoadSettings.Default,
                    ProjectCollection = projectCollection,
                    EvaluationContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared, fs),
                });
            }

            return null;
        }

        private Project? LoadProjectLegacy(string directory, string pathToFile, string? commitRef, bool fallbackToHead)
        {
            Commit? commit;
            
            using var repository = new Repository(directory);
            
            if (string.IsNullOrWhiteSpace(commitRef))
                commit = fallbackToHead ? repository.Head.Tip : null;
            else
                commit = GetCommitOrThrow(repository, commitRef);

            Stream GenerateStreamFromString(string s)
            {
                var stream = new MemoryStream();
                var writer = new StreamWriter(stream);
                writer.Write(s);
                writer.Flush();
                stream.Position = 0;
                return stream;
            }

            if (commit is null)
            {
                var path = Path.Combine(directory, pathToFile);
                if (!File.Exists(path)) return null;

                using var reader = new XmlTextReader(GenerateStreamFromString(File.ReadAllText(path)));
                return new Project(reader);
            }
            else
            {
                var path = Path.GetRelativePath(directory, pathToFile);
                var treeEntry = commit[path];
                if (treeEntry == null) return null;

                var blob = (Blob)treeEntry.Target;

                using var content = new StreamReader(blob.GetContentStream(), Encoding.UTF8);
                using var reader = new XmlTextReader(GenerateStreamFromString(content.ReadToEnd()));
                return new Project(reader);
            }
        }

        private static (Commit? From, Commit To) ParseRevisionRanges(
            Repository repository,
            string from,
            string to)
        {
            // Find the To Commit or use HEAD.
            var toCommit = GetCommitOrHead(repository, to);

            // No from: compare against working directory
            if (string.IsNullOrWhiteSpace(from))
            {
                // this.WriteLine($"Finding changes from working directory against {to}");
                return (null, toCommit);
            }

            var fromCommit = GetCommitOrThrow(repository, @from);
            return (fromCommit, toCommit);
        }

        private static T GetChangesForRange<T>(
            Repository repository,
            string from,
            string to)
            where T : class, IDiffResult
        {
            var (fromCommit, toCommit) = ParseRevisionRanges(repository, from, to);

            return fromCommit is null
                ? GetChangesAgainstWorkingDirectory<T>(repository, toCommit.Tree)
                : GetChangesBetweenTrees<T>(repository, fromCommit.Tree, toCommit.Tree);
        }

        private static T GetChangesAgainstWorkingDirectory<T>(
            Repository repository,
            Tree tree,
            IEnumerable<string>? files = null)
            where T : class, IDiffResult
        {
            return repository.Diff.Compare<T>(
                tree,
                DiffTargets.Index | DiffTargets.WorkingDirectory,
                files);
        }

        private static T GetChangesBetweenTrees<T>(
            Repository repository,
            Tree fromTree,
            Tree toTree,
            IEnumerable<string>? files = null)
            where T : class, IDiffResult
        {
            return repository.Diff.Compare<T>(
                fromTree,
                toTree,
                files);
        }

        private static Commit GetCommitOrHead(Repository repository, string name)
        {
            return string.IsNullOrWhiteSpace(name) ? repository.Head.Tip : GetCommitOrThrow(repository, name);
        }

        private static Commit GetCommitOrThrow(Repository repo, string name)
        {
            var commit = repo.Lookup<Commit>(name);
            if (commit != null)
            {
                return commit;
            }

            var branch = repo.Branches[name];
            if (branch != null)
            {
                return branch.Tip;
            }

            throw new InvalidOperationException(
                $"Couldn't find Git Commit or Branch with name {name} in repository {repo.Info.Path}");
        }

        private static IEnumerable<string> TreeChangesToPaths(
            TreeChanges changes,
            string repositoryRootPath)
        {
            foreach (var change in changes)
            {
                if (change == null) continue;

                var currentPath = Path.Combine(repositoryRootPath, change.Path);

                yield return currentPath;
            }
        }
    }
}
