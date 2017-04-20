﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Build.Framework;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Microsoft.Web.LibraryInstaller.Build.Test
{
    public class MockEngine : IBuildEngine5
    {
        public ICollection<BuildMessageEventArgs> Messages { get; } = new List<BuildMessageEventArgs>();
        public ICollection<BuildWarningEventArgs> Warnings { get; } = new List<BuildWarningEventArgs>();

        public bool IsRunningMultipleNodes => false;

        public bool ContinueOnError => false;

        public int LineNumberOfTaskNode => 0;

        public int ColumnNumberOfTaskNode => 0;

        public bool IsYielding { get; private set; }

        public string ProjectFileOfTaskNode => "<test>";

        public void LogMessageEvent(BuildMessageEventArgs e)
            => Messages.Add(e);

        public void LogWarningEvent(BuildWarningEventArgs e)
            => Warnings.Add(e);

        public void LogErrorEvent(BuildErrorEventArgs e)
        {
            Assert.IsFalse(true, e.Message);
        }

        #region NotImplemented

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs, string toolsVersion)
        {
            throw new NotImplementedException();
        }

        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
        {
            throw new NotImplementedException();
        }

        public BuildEngineResult BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, IDictionary[] globalProperties, IList<string>[] removeGlobalProperties, string[] toolsVersion, bool returnTargetOutputs)
        {
            throw new NotImplementedException();
        }

        public bool BuildProjectFilesInParallel(string[] projectFileNames, string[] targetNames, IDictionary[] globalProperties, IDictionary[] targetOutputsPerProject, string[] toolsVersion, bool useResultsCache, bool unloadProjectsOnCompletion)
        {
            throw new NotImplementedException();
        }

        public object GetRegisteredTaskObject(object key, RegisteredTaskObjectLifetime lifetime)
        {
            throw new NotImplementedException();
        }

        public void LogCustomEvent(CustomBuildEventArgs e)
        {
            throw new NotImplementedException();
        }

        public void LogTelemetry(string eventName, IDictionary<string, string> properties)
        {
            throw new NotImplementedException();
        }

        public void Reacquire()
        {
            IsYielding = false;
        }

        public void RegisterTaskObject(object key, object obj, RegisteredTaskObjectLifetime lifetime, bool allowEarlyCollection)
        {
            throw new NotImplementedException();
        }

        public object UnregisterTaskObject(object key, RegisteredTaskObjectLifetime lifetime)
        {
            throw new NotImplementedException();
        }

        public void Yield()
        {
            IsYielding = true;
        }
        #endregion
    }
}