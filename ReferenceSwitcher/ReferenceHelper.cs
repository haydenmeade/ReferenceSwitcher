﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Build.Evaluation;
using Microsoft.VisualStudio.Shell.Interop;
using VSLangProj;
using Project = EnvDTE.Project;

namespace ReferenceSwitcher
{
    public class ReferenceHelper
    {
        private readonly Func<string, string, bool> AskUserToProceed;

        public ReferenceHelper(Func<string, string, bool> AskUserToProceed)
        {
            if (AskUserToProceed == null) throw new ArgumentNullException("AskUserToProceed");
            this.AskUserToProceed = AskUserToProceed;
        }

        public void SolutionEvents_ProjectAdded(Project projectAdded)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            if (projectAdded.Object == null)
                return;

            var changes = FindChanges(projectAdded);

            SaveChanges(projectAdded, changes);

            StringBuilder sb = new StringBuilder();
            foreach (var item in changes)
            {
                sb.AppendFormat("Change {0} reference to {1} (Make it a project reference)\n", item.Project.Project.Name, item.ProjectToReference.Name);
            }

            if (!changes.Any())
                return;

            if (!AskUserToProceed("Switch File References to Project References?", sb.ToString()))
                return;


            //Apply changes
            foreach (var item in changes)
            {
                item.Reference.Remove();
	            try
	            {
		            item.Project.References.AddProject(item.ProjectToReference);
	            }
	            catch (Exception ex)
	            {
		            Trace.Write(ex.ToString());
					
					if(!AskUserToProceed(String.Format("Exceptional! We couldn't create a project reference from {0} to {1}",item.Project.Project.Name, item.ProjectToReference.Name),  ex.Message + "\n\n Click Yes or NO (I don't care)"))
						return;

		            Debugger.Break();
		            throw;
	            }
	            
            }
        }

        public void SolutionEvents_ProjectRemoved(Project project)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            Trace.WriteLine("Project Removed: " + project.Name);

            StorageProvider storage = new StorageProvider();

            IEnumerable<ProjectReferenceChange> items = storage.Load(project.DTE.Solution)
                                                               .GetProjectsThatReference(project.UniqueName);

            List<ProjectReferenceToRemove> workToDo = new List<ProjectReferenceToRemove>();

            foreach (var item in items)
            {
                Project projectToAddReferenceTo = null;
                foreach (Project p in project.DTE.Solution.Projects)
                {
                    if (p.UniqueName == item.SourceProject)
                    {
                        projectToAddReferenceTo = p;
                        break;
                    }
                }

                string filePath = item.KnownPaths;

                if (filePath == null)
                    continue;

                if (projectToAddReferenceTo != null)
                {
                    workToDo.Add(new ProjectReferenceToRemove
                        {
                            ProjectNeedingReference = projectToAddReferenceTo,
                            FilePath = filePath,
                            ProjectRemoving = project
                        });
                }
            }

            StringBuilder sb = new StringBuilder();
            foreach (var item in workToDo)
            {
                sb.AppendFormat("Switch {0} to refer back to a file reference: {1}\n\n", item.ProjectNeedingReference.Name, ToRelative(item.FilePath, item.ProjectNeedingReference.FullName));
            }

            if (workToDo.Count == 0)
                return;

            if(!AskUserToProceed("Switch Project Reference to File References?",  sb.ToString()))
                return;

            foreach (var item in workToDo)
            {
                AddReference(item.ProjectNeedingReference, item.ProjectRemoving, item.FilePath);
            }
        }

        private void AddReference(Project projectNeedingReference, Project projectRemoving, string filePath)
        {
            var vsProject = ((VSProject) projectNeedingReference.Object);
            filePath = ToAbsolute(filePath, vsProject.Project.FullName);

            foreach (Reference reference in vsProject.References)
            {
                if(reference.SourceProject == null)
                    continue;
                
                if(reference.SourceProject.FullName == projectRemoving.FullName)
                {
                    reference.Remove();
                    break;
                }
            }

            VSLangProj.Reference newRef = vsProject.References.Add(filePath);

            if (!newRef.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            {
                Microsoft.Build.Evaluation.Project msBuildProj = Microsoft.Build.Evaluation.ProjectCollection.GlobalProjectCollection.GetLoadedProjects(projectNeedingReference.FullName).First();
                Microsoft.Build.Evaluation.ProjectItem msBuildRef = null;

                AssemblyName newFileAssemblyName = AssemblyName.GetAssemblyName(filePath);
                foreach (var item in msBuildProj.GetItems("Reference"))
                {
                    AssemblyName refAssemblyName = null;
                    try
                    {
                        refAssemblyName = new AssemblyName(item.EvaluatedInclude);
                    }
                    catch { }

                    if (refAssemblyName != null)
                    {
                        var refToken = refAssemblyName.GetPublicKeyToken() ?? new byte[0];
                        var newToken = newFileAssemblyName.GetPublicKeyToken() ?? new byte[0];

                        if
                        (
                            refAssemblyName.Name.Equals(newFileAssemblyName.Name, StringComparison.OrdinalIgnoreCase)
                            && ((refAssemblyName.Version != null && refAssemblyName.Version.Equals(newFileAssemblyName.Version))
                                || (refAssemblyName.Version == null && newFileAssemblyName.Version == null))
                            && (refAssemblyName.CultureInfo != null && (refAssemblyName.CultureInfo.Equals(newFileAssemblyName.CultureInfo))
                                || (refAssemblyName.CultureInfo == null && newFileAssemblyName.CultureInfo == null))
                            && ((Enumerable.SequenceEqual(refToken, newToken)))
                        )
                        {
                            msBuildRef = item;
                            break;
                        }
                    }
                }

                if (msBuildRef != null)
                {
                    _ = msBuildRef.SetMetadataValue("HintPath", ToRelative(filePath, projectNeedingReference.FullName));
                }
            }
        }

        private static string ToAbsolute(string filePath, string relativeTo)
        {
            if (Path.IsPathRooted(filePath))
                return filePath;

            var fullPath = Path.Combine(Path.GetDirectoryName(relativeTo), filePath);
            return Path.GetFullPath(fullPath);
        }

        private static string ToRelative(string filePath, string relativeTo)
        {
            if (!Path.IsPathRooted(filePath))
                return filePath;

            Uri newFileUri = new Uri(filePath);
            Uri projectUri = new Uri(Path.GetDirectoryName(relativeTo).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
           
            Uri relativeUri = projectUri.MakeRelativeUri(newFileUri);

            return relativeUri.ToString();
        }

        private static IEnumerable<ProjectReferenceToAdd> FindChanges(Project projectAdded)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            string assemblyName = projectAdded.GetAssemblyName();

            var solution = projectAdded.DTE.Solution;

            Microsoft.Build.Evaluation.Project buildProjAdded =
                   ProjectCollection.GlobalProjectCollection.GetLoadedProjects(projectAdded.FullName).First();

            var changes = new List<ProjectReferenceToAdd>();

            foreach (Project project in solution.Projects)
            {
                if (project.UniqueName == projectAdded.UniqueName)
                    continue;

                VSProject vsProject = project.Object as VSProject;

                if (vsProject == null) continue;

                foreach (Reference reference in vsProject.References)
                {
					
					//If this project already has a project reference to the project being added then we don't need to do anything.
					if(reference.SourceProject != null && projectAdded == reference.SourceProject) break;

                    if (Path.GetFileNameWithoutExtension(reference.Path).Equals(assemblyName))
                    {
                        changes.Add(new ProjectReferenceToAdd
                        {
                            Reference = reference,
                            Project = vsProject,
                            ProjectToReference = projectAdded
                        }
                            );
                    }
                }
            }
            return changes;
        }

        private static void SaveChanges(Project projectAdded, IEnumerable<ProjectReferenceToAdd> changes)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            var storage = new StorageProvider();

            var referenceChanges = storage.Load(projectAdded.DTE.Solution);

            foreach (var item in changes)
            {
                referenceChanges.AddUpdate(item.Project.Project.UniqueName, item.ProjectToReference.UniqueName,
                                           ToRelative(item.Reference.Path, item.Project.Project.FullName));
            }

            storage.Save(projectAdded.DTE.Solution, referenceChanges);
        }

        public class ProjectReferenceToRemove
        {
            public Project ProjectNeedingReference { get; set; }
            public Project ProjectRemoving { get; set; }
            public string FilePath { get; set; }
        }

        public class ProjectReferenceToAdd
        {
            public Reference Reference { get; set; }

            public VSProject Project { get; set; }

            public Project ProjectToReference { get; set; }
        }
    }
}
