// <copyright file="ProjectReference.cs" company="David Federman">
// Copyright (c) David Federman. All rights reserved.
// </copyright>

namespace ReferenceTrimmer
{
    internal sealed class ProjectReference
    {
        public ProjectReference(ParsedProject project, string unevaluatedInclude)
        {
            this.Project = project;
            this.UnevaluatedInclude = unevaluatedInclude;
        }

        public ParsedProject Project { get; }

        public string UnevaluatedInclude { get; }
    }
}
