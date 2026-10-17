using GibiOMatic.App.Services;
using GibiOMatic.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GibiOMatic.App.Tests;

/// <summary>The real composition root over a temp folder, with a scripted folder picker.</summary>
public sealed class AppTestContext : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServiceProvider _services;

    public AppTestContext()
    {
        Paths = new AppPaths(_temp.Combine("config"), _temp.Combine("data"));
        _services = CreateServices();
    }

    public AppPaths Paths { get; }

    public FakeFolderPicker Picker { get; } = new();

    public FakeFilePicker FilePicker { get; } = new();

    public string Folder(string name) => _temp.Combine(name);

    public T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    /// <summary>A second "run" of the app on the same machine (same settings and index folders).</summary>
    public ServiceProvider CreateServices() =>
        new ServiceCollection()
            .AddGibiOMatic(Paths, new ConfigurationBuilder().Build())
            .AddSingleton<IFolderPicker>(Picker)
            .AddSingleton<IFilePicker>(FilePicker)
            .AddSingleton<IUiThread>(new InlineUiThread())
            .BuildServiceProvider();

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }
}

public sealed class FakeFolderPicker : IFolderPicker
{
    private readonly Queue<string?> _answers = new();

    public void WillReturn(string? folder) => _answers.Enqueue(folder);

    public Task<string?> PickFolderAsync(string title, CancellationToken ct = default) =>
        Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
}

public sealed class FakeFilePicker : IFilePicker
{
    private readonly Queue<IReadOnlyList<string>> _answers = new();

    public void WillReturn(params string[] files) => _answers.Enqueue(files);

    public Task<IReadOnlyList<string>> PickBooksAsync(string title, CancellationToken ct = default) =>
        Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : (IReadOnlyList<string>)[]);
}

/// <summary>No UI thread in tests: updates run right away.</summary>
public sealed class InlineUiThread : IUiThread
{
    public void Post(Action action) => action();
}
