using PetTracker.Core.Startup;

namespace PetTracker.Tests;

public class AutoStartTests
{
    private sealed class FakeRunKey : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Get(string name) => Values.GetValueOrDefault(name);
        public void Set(string name, string command) => Values[name] = command;
        public void Remove(string name) => Values.Remove(name);
    }

    private const string Exe = @"C:\Users\Someone\AppData\Local\Programs\Aion2PetTracker\PetTracker.App.exe";

    [Fact]
    public void CommandIsTheQuotedExePathPlusMinimized()
    {
        Assert.Equal($"\"{Exe}\" --minimized", AutoStart.BuildCommand(Exe));
        Assert.Equal($"\"{Exe}\" --minimized", AutoStart.BuildCommand($"\"{Exe}\"")); // already quoted
    }

    [Fact]
    public void PathsWithSpacesStayQuoted()
    {
        const string spaced = @"D:\My Games\Pet Tracker\PetTracker.App.exe";
        Assert.Equal(@"""D:\My Games\Pet Tracker\PetTracker.App.exe"" --minimized", AutoStart.BuildCommand(spaced));
    }

    [Fact]
    public void EmptyPathIsRejected() => Assert.Throws<ArgumentException>(() => AutoStart.BuildCommand(" "));

    [Fact]
    public void EnableWritesOneValueAndDisableRemovesIt()
    {
        var store = new FakeRunKey();
        var autoStart = new AutoStart(store);
        Assert.False(autoStart.IsEnabled);

        autoStart.SetEnabled(true, Exe);

        Assert.True(autoStart.IsEnabled);
        Assert.Equal([AutoStart.ValueName], store.Values.Keys);
        Assert.Equal("Aion2PetTracker", AutoStart.ValueName);
        Assert.Equal(AutoStart.BuildCommand(Exe), store.Values["Aion2PetTracker"]);
        Assert.True(autoStart.IsEnabledFor(Exe));
        Assert.False(autoStart.IsEnabledFor(@"C:\Elsewhere\PetTracker.App.exe"));

        autoStart.SetEnabled(false, Exe);

        Assert.False(autoStart.IsEnabled);
        Assert.Empty(store.Values);
    }

    [Fact]
    public void DisableWithNothingThereIsFine()
    {
        var autoStart = new AutoStart(new FakeRunKey());
        autoStart.SetEnabled(false, null);
        Assert.False(autoStart.IsEnabled);
    }

    [Theory]
    [InlineData(@"C:\x\PetTracker.App.exe", true)]
    [InlineData(@"C:\x\petTracker.app.EXE", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)]
    [InlineData(@"C:\x\PetTracker.App.dll", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void OnlyTheRealAppExeCanRegister(string? path, bool expected) => Assert.Equal(expected, AutoStart.CanRegister(path));

    [Fact]
    public void EnablingForAHostProcessThrowsAndWritesNothing()
    {
        var store = new FakeRunKey();
        Assert.Throws<InvalidOperationException>(() => new AutoStart(store).SetEnabled(true, @"C:\Program Files\dotnet\dotnet.exe"));
        Assert.Empty(store.Values);
    }

    [Fact]
    public void RunKeyPathIsUnderCurrentUser() =>
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RegistryRunKeyStore.KeyPath);
}

public class StartupOptionsTests
{
    [Fact]
    public void ParsesTheDocumentedSwitches()
    {
        var o = StartupOptions.Parse(["--data", @"C:\tmp\x", "--exit-after", "5", "--minimized"]);
        Assert.Equal(@"C:\tmp\x", o.DataFolder);
        Assert.Equal(5, o.ExitAfterSeconds);
        Assert.True(o.Minimized);
        Assert.False(o.Quit);
    }

    [Fact]
    public void NoArgumentsMeansDefaults()
    {
        var o = StartupOptions.Parse([]);
        Assert.Null(o.DataFolder);
        Assert.Equal(0, o.ExitAfterSeconds);
        Assert.False(o.Minimized);
        Assert.False(o.Quit);
    }

    [Fact]
    public void QuitAndBadNumbersAreHandled()
    {
        var o = StartupOptions.Parse(["--QUIT", "--exit-after", "abc", "--data"]);
        Assert.True(o.Quit);
        Assert.Equal(0, o.ExitAfterSeconds);
        Assert.Null(o.DataFolder); // value missing
    }

    [Fact]
    public void TheAutoStartCommandRoundTripsThroughTheParser()
    {
        var command = AutoStart.BuildCommand(@"C:\x\PetTracker.App.exe");
        Assert.True(StartupOptions.Parse(command.Split(' ')[^1..]).Minimized);
    }
}

public class SingleInstanceGuardTests
{
    private static string UniqueKey() => "test" + Guid.NewGuid().ToString("N");

    [Fact]
    public void SecondGuardWithTheSameKeyIsNotFirst()
    {
        var key = UniqueKey();
        using var first = new SingleInstanceGuard(key);
        using var second = new SingleInstanceGuard(key);
        Assert.True(first.IsFirst);
        Assert.False(second.IsFirst);
    }

    [Fact]
    public void DifferentKeysAreIndependent()
    {
        using var a = new SingleInstanceGuard(UniqueKey());
        using var b = new SingleInstanceGuard(UniqueKey());
        Assert.True(a.IsFirst);
        Assert.True(b.IsFirst);
    }

    [Fact]
    public void SecondLaunchCanAskTheFirstToShowOrQuit()
    {
        var key = UniqueKey();
        using var first = new SingleInstanceGuard(key);
        using var shown = new ManualResetEventSlim();
        using var quit = new ManualResetEventSlim();
        first.Listen(shown.Set, quit.Set);

        using var second = new SingleInstanceGuard(key);
        second.SignalShow();
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(quit.IsSet);
        second.SignalQuit();
        Assert.True(quit.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void KeyDependsOnTheDataFolderOnly()
    {
        Assert.Equal(SingleInstanceGuard.KeyFor(@"C:\Data\X"), SingleInstanceGuard.KeyFor(@"c:\data\x\"));
        Assert.NotEqual(SingleInstanceGuard.KeyFor(@"C:\Data\X"), SingleInstanceGuard.KeyFor(@"C:\Data\Y"));
    }
}
