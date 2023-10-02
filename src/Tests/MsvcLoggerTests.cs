using Microsoft.Build.Framework;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    public void UnusedLibsLogger_ForwardsNothingIfLinkTaskNotStarted()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        using var logger = new UnusedLibsLogger(eventSource, eventRedirector);
        eventSource.AssertExpectedForwardingEventSubscriptions();
        eventSource.SendTaskStarted(new TaskStartedEventArgs(message: "Not Link!", helpKeyword: "NotLink", projectFile: "a.proj", taskFile: "a.proj", taskName: "NotLink"));
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "a non-link message", helpKeyword: "NotLink", senderName: "NotLink", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendTaskFinished(new TaskFinishedEventArgs(message: "Not Link!", helpKeyword: "NotLink", projectFile: "a.proj", taskFile: "a.proj", taskName: "NotLink", succeeded: true));
        Assert.AreEqual(0, eventRedirector.Events.Count);
    }

    [TestMethod]
    public void UnusedLibsLogger_ForwardsNothingIfLinkTaskGetsNoUnusedLibMessages()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        using var logger = new UnusedLibsLogger(eventSource, eventRedirector);
        eventSource.AssertExpectedForwardingEventSubscriptions();
        eventSource.SendTaskStarted(new TaskStartedEventArgs(message: "Link starting", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link"));
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "Generic link message", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendTaskFinished(new TaskFinishedEventArgs(message: "Link finished", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link", succeeded: true));
        Assert.AreEqual(0, eventRedirector.Events.Count);
    }

    [TestMethod]
    public void UnusedLibsLogger_ForwardsNothingIfLinkTaskGetsUnusedLibHeaderOnly()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        using var logger = new UnusedLibsLogger(eventSource, eventRedirector);
        eventSource.AssertExpectedForwardingEventSubscriptions();
        eventSource.SendTaskStarted(new TaskStartedEventArgs(message: "Link starting", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link"));
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "Unused libraries:", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendTaskFinished(new TaskFinishedEventArgs(message: "Link finished", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link", succeeded: true));
        Assert.AreEqual(0, eventRedirector.Events.Count);
    }

    [TestMethod]
    public void UnusedLibsLogger_ForwardsUnusedLibs()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        using var logger = new UnusedLibsLogger(eventSource, eventRedirector);
        eventSource.AssertExpectedForwardingEventSubscriptions();
        eventSource.SendTaskStarted(new TaskStartedEventArgs(message: "Link starting", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link"));
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "Unused libraries:", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "  user32.lib", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "  bar.lib", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: string.Empty, helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendTaskFinished(new TaskFinishedEventArgs(message: "Link finished", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link", succeeded: true));
        Assert.AreEqual(1, eventRedirector.Events.Count);
        var unusedLibArgs = eventRedirector.Events[0] as UnusedLibsCustomBuildEventArgs;
        Assert.IsNotNull(unusedLibArgs);
        Assert.AreEqual("a.proj", unusedLibArgs.ProjectPath);
        Assert.IsTrue(unusedLibArgs.Message.Contains("user32.lib", StringComparison.Ordinal), unusedLibArgs.Message);
        Assert.IsTrue(unusedLibArgs.Message.Contains("bar.lib", StringComparison.Ordinal), unusedLibArgs.Message);
        Assert.IsTrue(unusedLibArgs.UnusedLibraryPathsJson.Length > 0);
    }

    [TestMethod]
    public void UnusedLibsLogger_ForwardsNothingIfLinkTaskFails()
    {
        var eventSource = new MockEventSource();
        var eventRedirector = new MockEventRedirector();
        using var logger = new UnusedLibsLogger(eventSource, eventRedirector);
        eventSource.AssertExpectedForwardingEventSubscriptions();
        eventSource.SendTaskStarted(new TaskStartedEventArgs(message: "Link starting", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link"));
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "Unused libraries:", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "  kernel32.lib", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: "  foo.lib", helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendMessageRaised(new BuildMessageEventArgs(message: string.Empty, helpKeyword: "Link", senderName: "Link", MessageImportance.High, DateTime.Now) { ProjectFile = "a.proj" });
        Assert.AreEqual(0, eventRedirector.Events.Count);
        eventSource.SendTaskFinished(new TaskFinishedEventArgs(message: "Link finished", helpKeyword: "Link", projectFile: "a.proj", taskFile: "a.proj", taskName: "Link", succeeded: false));
        Assert.AreEqual(0, eventRedirector.Events.Count);
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

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
