using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Data;
using PocketReader.Models;
using Windows.Foundation;

namespace PocketReader;

/// <summary>
/// Display collection that materializes <see cref="ArticleViewModel"/>s lazily as the
/// ListView/GridView scrolls. A 20k-item view paints its first screenful instantly
/// instead of building every row (and every binding) up front — the WinUI equivalent
/// of a windowed/virtualized list.
/// </summary>
public class IncrementalArticleSource : ObservableCollection<ArticleViewModel>, ISupportIncrementalLoading
{
    private IReadOnlyList<Article> _all = Array.Empty<Article>();
    private int _index;

    // First page is sized to comfortably fill a tall window on first paint; later
    // pages are pulled on demand as the user scrolls toward the end.
    private const int InitialPage = 120;
    private const int Page = 100;

    /// <summary>Point the collection at a new result set and seed the first screenful.</summary>
    public void Load(IReadOnlyList<Article> all)
    {
        _all = all ?? Array.Empty<Article>();
        _index = 0;
        Clear();              // one Reset
        AddPage(InitialPage); // instant first paint
    }

    public bool HasMoreItems => _index < _all.Count;

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count)
    {
        return AsyncInfo.Run(_ =>
        {
            var added = AddPage(Page);
            return Task.FromResult(new LoadMoreItemsResult { Count = (uint)added });
        });
    }

    private int AddPage(int size)
    {
        var remaining = _all.Count - _index;
        var take = Math.Min(size, remaining);
        if (take <= 0) return 0;
        for (var i = 0; i < take; i++)
            Add(new ArticleViewModel(_all[_index + i]));
        _index += take;
        return take;
    }
}
