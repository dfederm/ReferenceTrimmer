using Microsoft.Build.Framework;
using ReferenceTrimmer.Loggers.MSVC;

#pragma warning disable CA1707  // Underscores in test names

namespace ReferenceTrimmer.Tests;

[TestClass]
public sealed class MsvcLoggerTests
{
    private sealed class MockEventSource : IEventSource
    {
        public event BuildMessageEventHandler? MessageRaised;
        public event BuildErrorEventHandler? ErrorRaised;
        public event BuildWarningEventHandler? WarningRaised;
        public event BuildStartedEventHandler? BuildStarted;
        public event BuildFinishedEventHandler? BuildFinished;
        public event ProjectStartedEventHandler? ProjectStarted;
        public event ProjectFinishedEventHandler? ProjectFinished;
        public event TargetStartedEventHandler? TargetStarted;
        public event TargetFinishedEventHandler? TargetFinished;
        public event TaskStartedEventHandler? TaskStarted;
        public event TaskFinishedEventHandler? TaskFinished;
        public event CustomBuildEventHandler? CustomEventRaised;
        public event BuildStatusEventHandler? StatusEventRaised;
        public event AnyEventHandler? AnyEventRaised;

        public void AssertExpectedForwardingEventSubscriptions()
        {
            Assert.IsNotNull(MessageRaised);
            Assert.IsNotNull(TaskStarted);
            Assert.IsNotNull(TaskFinished);
        }

        public void AssertExpectedCentralLoggerEventSubscriptions()
        {
            Assert.IsNotNull(CustomEventRaised);
        }

        public void SendTaskStarted(TaskStartedEventArgs e)
        {
            TaskStarted?.Invoke(this, e);
        }

        public void SendTaskFinished(TaskFinishedEventArgs e)
        {
            TaskFinished?.Invoke(this, e);
        }

        public void SendMessageRaised(BuildMessageEventArgs e)
        {
            MessageRaised?.Invoke(this, e);
        }

        public void SendCustomEvent(CustomBuildEventArgs e)
        {
            CustomEventRaised?.Invoke(this, e);
        }
    }

    private sealed class MockEventRedirector : IEventRedirector
    {
        public List<BuildEventArgs> Events { get; } = new();

        public void ForwardEvent(BuildEventArgs buildEvent)
        {
            Events.Add(buildEvent);
        }
    }

    private sealed class NonUnusedLibCustomEventArgs : CustomBuildEventArgs
    {
    }

    [TestMethod]
    public void ForwardingLoggerInitShutdown()
    {
        var eventRedirector = new MockEventRedirector();
        var forwardingLogger = new ForwardingLogger
        {
            BuildEventRedirector = eventRedirector,
            NodeId = 1,
            Verbosity = LoggerVerbosity.Normal
        };

        var eventSource = new MockEventSource();
        forwardingLogger.Initialize(eventSource, nodeCount: 2);
        eventSource.AssertExpectedForwardingEventSubscriptions();
        forwardingLogger.Shutdown();
    }

    [TestMethod]
    public void ForwardingLogger_ForwardsNothingIfLinkTaskNotStarted()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        var logger = new ForwardingLogger { BuildEventRedirector = eventRedirector };
        logger.Initialize(eventSource);
        try
        {
            eventSource.AssertExpectedForwardingEventSubscriptions();
            eventSource.SendTaskStarted(new TaskStartedEventArgs(message: "Not Link!", helpKeyword: "NotLink", projectFile: "a.proj", taskFile: "a.proj", taskName: "NotLink"));
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "a non-link message", helpKeyword: "NotLink", senderName: "NotLink", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendTaskFinished(new TaskFinishedEventArgs(message: "Not Link!", helpKeyword: "NotLink", projectFile: "a.proj", taskFile: "a.proj", taskName: "NotLink", succeeded: true));
            Assert.IsEmpty(eventRedirector.Events);
        }
        finally
        {
            logger.Shutdown();
        }
    }

    [TestMethod]
    public void ForwardingLogger_ForwardsNothingIfLinkTaskGetsNoUnusedLibMessages()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        var logger = new ForwardingLogger { BuildEventRedirector = eventRedirector };
        logger.Initialize(eventSource);
        try
        {
            eventSource.AssertExpectedForwardingEventSubscriptions();
            eventSource.SendTaskStarted(new TaskStartedEventArgs(message: "Link starting", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link"));
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "Generic link message", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendTaskFinished(new TaskFinishedEventArgs(message: "Link finished", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link", succeeded: true));
            Assert.IsEmpty(eventRedirector.Events);
        }
        finally
        {
            logger.Shutdown();
        }
    }

    [TestMethod]
    public void ForwardingLogger_ForwardsNothingIfLinkTaskGetsUnusedLibHeaderOnly()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        var logger = new ForwardingLogger { BuildEventRedirector = eventRedirector };
        logger.Initialize(eventSource);
        try
        {
            eventSource.AssertExpectedForwardingEventSubscriptions();
            eventSource.SendTaskStarted(new TaskStartedEventArgs(message: "Link starting", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link"));
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "Unused libraries:", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendTaskFinished(new TaskFinishedEventArgs(message: "Link finished", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link", succeeded: true));
            Assert.IsEmpty(eventRedirector.Events);
        }
        finally
        {
            logger.Shutdown();
        }
    }

    [TestMethod]
    public void ForwardingLogger_ForwardsUnusedLibs()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        var logger = new ForwardingLogger { BuildEventRedirector = eventRedirector };
        logger.Initialize(eventSource);
        try
        {
            eventSource.AssertExpectedForwardingEventSubscriptions();
            eventSource.SendTaskStarted(new TaskStartedEventArgs(message: "Link starting", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link"));
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "Unused libraries:", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "  user32.lib", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "  bar.lib", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: string.Empty, helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendTaskFinished(new TaskFinishedEventArgs(message: "Link finished", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link", succeeded: true));
            Assert.HasCount(1, eventRedirector.Events);
            var unusedLibArgs = eventRedirector.Events[0] as UnusedLibsCustomBuildEventArgs;
            Assert.IsNotNull(unusedLibArgs);
            Assert.AreEqual("a.proj", unusedLibArgs.ProjectPath);
            Assert.IsTrue(unusedLibArgs.Message!.Contains("user32.lib", StringComparison.Ordinal), unusedLibArgs.Message);
            Assert.IsTrue(unusedLibArgs.Message.Contains("bar.lib", StringComparison.Ordinal), unusedLibArgs.Message);
            Assert.IsGreaterThan(0, unusedLibArgs.UnusedLibraryPathsJson.Length);
        }
        finally
        {
            logger.Shutdown();
        }
    }

    [TestMethod]
    public void ForwardingLogger_IsolatesConcurrentBuildContextsForSameProject()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        var logger = new ForwardingLogger { BuildEventRedirector = eventRedirector };
        logger.Initialize(eventSource);
        var firstTaskContext = new BuildEventContext(nodeId: 1, targetId: 2, projectContextId: 3, taskId: 4);
        var firstMessageContext = new BuildEventContext(nodeId: 1, targetId: 2, projectContextId: 3, taskId: 5);
        var secondTaskContext = new BuildEventContext(nodeId: 1, targetId: 6, projectContextId: 7, taskId: 8);
        var secondMessageContext = new BuildEventContext(nodeId: 1, targetId: 6, projectContextId: 7, taskId: 9);
        try
        {
            SendLinkTaskStarted(eventSource, "same.proj", firstTaskContext);
            SendLinkTaskStarted(eventSource, "same.proj", secondTaskContext);
            SendLinkMessage(eventSource, "same.proj", "Unused libraries:", firstMessageContext);
            SendLinkMessage(eventSource, "same.proj", "Unused libraries:", secondMessageContext);
            SendLinkMessage(eventSource, "same.proj", "  first.lib", firstMessageContext);
            SendLinkMessage(eventSource, "same.proj", "  second.lib", secondMessageContext);
            SendLinkTaskFinished(eventSource, string.Empty, firstTaskContext);
            SendLinkTaskFinished(eventSource, string.Empty, secondTaskContext);

            Assert.HasCount(2, eventRedirector.Events);
            UnusedLibsCustomBuildEventArgs firstEvent = GetUnusedLibEvent(eventRedirector.Events[0]);
            UnusedLibsCustomBuildEventArgs secondEvent = GetUnusedLibEvent(eventRedirector.Events[1]);
            Assert.Contains("first.lib", firstEvent.UnusedLibraryPathsJson);
            Assert.IsFalse(firstEvent.UnusedLibraryPathsJson.Contains("second.lib", StringComparison.Ordinal));
            Assert.Contains("second.lib", secondEvent.UnusedLibraryPathsJson);
            Assert.IsFalse(secondEvent.UnusedLibraryPathsJson.Contains("first.lib", StringComparison.Ordinal));
        }
        finally
        {
            logger.Shutdown();
        }
    }

    [TestMethod]
    public void ForwardingLogger_FallsBackToProjectPathForPartialBuildContexts()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        var logger = new ForwardingLogger { BuildEventRedirector = eventRedirector };
        logger.Initialize(eventSource);
        var firstContext = new BuildEventContext(
            nodeId: 1,
            targetId: 2,
            projectContextId: BuildEventContext.InvalidProjectContextId,
            taskId: 3);
        var secondContext = new BuildEventContext(
            nodeId: 1,
            targetId: 4,
            projectContextId: BuildEventContext.InvalidProjectContextId,
            taskId: 5);
        try
        {
            SendLinkTaskStarted(eventSource, "first.proj", firstContext);
            SendLinkTaskStarted(eventSource, "second.proj", secondContext);
            SendLinkMessage(eventSource, "first.proj", "Unused libraries:", firstContext);
            SendLinkMessage(eventSource, "second.proj", "Unused libraries:", secondContext);
            SendLinkMessage(eventSource, "first.proj", "  first.lib", firstContext);
            SendLinkMessage(eventSource, "second.proj", "  second.lib", secondContext);
            SendLinkTaskFinished(eventSource, "first.proj", firstContext);
            SendLinkTaskFinished(eventSource, "second.proj", secondContext);

            Assert.HasCount(2, eventRedirector.Events);
            UnusedLibsCustomBuildEventArgs firstEvent = GetUnusedLibEvent(eventRedirector.Events[0]);
            UnusedLibsCustomBuildEventArgs secondEvent = GetUnusedLibEvent(eventRedirector.Events[1]);
            Assert.Contains("first.lib", firstEvent.UnusedLibraryPathsJson);
            Assert.IsFalse(firstEvent.UnusedLibraryPathsJson.Contains("second.lib", StringComparison.Ordinal));
            Assert.Contains("second.lib", secondEvent.UnusedLibraryPathsJson);
            Assert.IsFalse(secondEvent.UnusedLibraryPathsJson.Contains("first.lib", StringComparison.Ordinal));
        }
        finally
        {
            logger.Shutdown();
        }
    }

    [TestMethod]
    public void ForwardingLogger_ForwardsNothingIfLinkTaskFails()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        var logger = new ForwardingLogger { BuildEventRedirector = eventRedirector };
        logger.Initialize(eventSource);
        try
        {
            eventSource.AssertExpectedForwardingEventSubscriptions();
            eventSource.SendTaskStarted(new TaskStartedEventArgs(message: "Link starting", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link"));
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "Unused libraries:", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "  kernel32.lib", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "  foo.lib", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendMessageRaised(new BuildMessageEventArgs(message: string.Empty, helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
            Assert.IsEmpty(eventRedirector.Events);
            eventSource.SendTaskFinished(new TaskFinishedEventArgs(message: "Link finished", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link", succeeded: false));
            Assert.IsEmpty(eventRedirector.Events);
        }
        finally
        {
            logger.Shutdown();
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void CentralLogger_WritesNoJsonIfNoUnusedLibEvents()
    {
        string jsonPath = Path.Combine(Environment.CurrentDirectory, CentralLogger.JsonLogFileName);
        DeleteIfExists(jsonPath);

        var eventSource = new MockEventSource();
        var centralLogger = new CentralLogger();
        centralLogger.Initialize(eventSource);
        eventSource.AssertExpectedCentralLoggerEventSubscriptions();
        eventSource.SendCustomEvent(new NonUnusedLibCustomEventArgs());
        eventSource.SendCustomEvent(new UnusedLibsCustomBuildEventArgs());
        centralLogger.Shutdown();
        Assert.IsFalse(File.Exists(jsonPath));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task CentralLogger_ProcessesUnusedLibEventsFromPrimaryNode()
    {
        string jsonPath = Path.Combine(Environment.CurrentDirectory, CentralLogger.JsonLogFileName);
        DeleteIfExists(jsonPath);

        var eventSource = new MockEventSource();
        var centralLogger = new CentralLogger();
        centralLogger.Initialize(eventSource);
        eventSource.AssertExpectedCentralLoggerEventSubscriptions();
        eventSource.AssertExpectedForwardingEventSubscriptions();
        eventSource.SendTaskStarted(new TaskStartedEventArgs(
            message: "Link starting",
            helpKeyword: "Link",
            projectFile: "a.proj",
            taskFile: "a.proj",
            taskName: "Link"));
        eventSource.SendMessageRaised(new BuildMessageEventArgs(
            message: "Unused libraries:",
            helpKeyword: "Link",
            senderName: "Link",
            MessageImportance.High,
            DateTime.Now)
        {
            ProjectFile = "a.proj",
        });
        eventSource.SendMessageRaised(new BuildMessageEventArgs(
            message: "  user32.lib",
            helpKeyword: "Link",
            senderName: "Link",
            MessageImportance.High,
            DateTime.Now)
        {
            ProjectFile = "a.proj",
        });
        eventSource.SendMessageRaised(new BuildMessageEventArgs(
            message: string.Empty,
            helpKeyword: "Link",
            senderName: "Link",
            MessageImportance.High,
            DateTime.Now)
        {
            ProjectFile = "a.proj",
        });
        eventSource.SendTaskFinished(new TaskFinishedEventArgs(
            message: "Link finished",
            helpKeyword: "Link",
            projectFile: "a.proj",
            taskFile: "a.proj",
            taskName: "Link",
            succeeded: true));
        centralLogger.Shutdown();

        Assert.IsTrue(File.Exists(jsonPath));
        string json = await File.ReadAllTextAsync(jsonPath);
        Assert.Contains("user32.lib", json);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task CentralLogger_JsonOnUnusedLibEvents()
    {
        string jsonPath = Path.Combine(Environment.CurrentDirectory, CentralLogger.JsonLogFileName);
        DeleteIfExists(jsonPath);

        var eventSource = new MockEventSource();
        var centralLogger = new CentralLogger();
        centralLogger.Initialize(eventSource);
        eventSource.AssertExpectedCentralLoggerEventSubscriptions();
        eventSource.SendCustomEvent(new UnusedLibsCustomBuildEventArgs(message: "Unused libraries!",
            projectPath: "a.proj", unusedLibraryPathsJson: "{ \"aProp\": \"aValue\" }"));
        eventSource.SendCustomEvent(new UnusedLibsCustomBuildEventArgs(message: "Unused libraries 2!",
            projectPath: "a2.proj", unusedLibraryPathsJson: "{ \"aProp2\": \"aValue2\" }"));
        centralLogger.Shutdown();
        Assert.IsTrue(File.Exists(jsonPath));
        Assert.AreEqual($"[{Environment.NewLine}{{ \"aProp\": \"aValue\" }},{Environment.NewLine}{{ \"aProp2\": \"aValue2\" }}{Environment.NewLine}]{Environment.NewLine}",
            await File.ReadAllTextAsync(jsonPath));
    }

    private static void SendLinkTaskStarted(
        MockEventSource eventSource,
        string projectFile,
        BuildEventContext context)
    {
        eventSource.SendTaskStarted(new TaskStartedEventArgs(
            message: "Link starting",
            helpKeyword: "Link",
            projectFile: projectFile,
            taskFile: projectFile,
            taskName: "Link")
        {
            BuildEventContext = context,
        });
    }

    private static void SendLinkMessage(
        MockEventSource eventSource,
        string projectFile,
        string message,
        BuildEventContext context)
    {
        eventSource.SendMessageRaised(new BuildMessageEventArgs(
            message: message,
            helpKeyword: "Link",
            senderName: "Link",
            MessageImportance.High,
            DateTime.Now)
        {
            BuildEventContext = context,
            ProjectFile = projectFile,
        });
    }

    private static void SendLinkTaskFinished(
        MockEventSource eventSource,
        string projectFile,
        BuildEventContext context)
    {
        eventSource.SendTaskFinished(new TaskFinishedEventArgs(
            message: "Link finished",
            helpKeyword: "Link",
            projectFile: projectFile,
            taskFile: projectFile,
            taskName: "Link",
            succeeded: true)
        {
            BuildEventContext = context,
        });
    }

    private static UnusedLibsCustomBuildEventArgs GetUnusedLibEvent(BuildEventArgs buildEvent)
    {
        var unusedLibEvent = buildEvent as UnusedLibsCustomBuildEventArgs;
        Assert.IsNotNull(unusedLibEvent);
        return unusedLibEvent;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
