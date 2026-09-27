using System.Collections.ObjectModel;
using IKEMENLab.App.Infrastructure;
using IKEMENLab.Core.Models;

namespace IKEMENLab.App.ViewModels;

public sealed class CharactersViewModel : ObservableObject
{
    private readonly List<CharacterCardViewModel> _all = [];
    private string _searchText = string.Empty;

    public ObservableCollection<CharacterCardViewModel> Characters { get; } = [];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public int TotalCount => _all.Count;

    public void ApplySnapshot(LibrarySnapshot? snapshot)
    {
        _all.Clear();
        Characters.Clear();
        if (snapshot is null) return;

        foreach (var entry in snapshot.Characters)
        {
            _all.Add(new CharacterCardViewModel(entry));
        }

        ApplyFilter();
        OnPropertyChanged(nameof(TotalCount));
    }

    private void ApplyFilter()
    {
        Characters.Clear();
        var q = SearchText.Trim();
        IEnumerable<CharacterCardViewModel> query = _all;
        if (!string.IsNullOrEmpty(q))
        {
            query = _all.Where(c =>
                c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.Author.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.FolderId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                c.DefPath.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in query)
        {
            Characters.Add(item);
        }
    }
}
