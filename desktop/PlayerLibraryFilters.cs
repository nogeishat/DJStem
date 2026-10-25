using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

internal sealed partial class PlayerForm
{
    private readonly GlassPanel libraryBpmBar = new GlassPanel();
    private readonly GlassButton libraryGridView = new GlassButton(), libraryListView = new GlassButton();
    private bool libraryListMode;
    private bool LibraryUsesListView { get { return !playlistsView && libraryListMode && (!artistsView || artistFolder != null); } }
    private readonly ComboBox libraryBpmSort = new GlassComboBox();
    private readonly CheckBox libraryBpmRange = new CheckBox();
    private readonly NumericUpDown libraryBpmFrom = new GlassNumericUpDown { Minimum = 30, Maximum = 300, DecimalPlaces = 2, Value = 30, Increment = 1 };
    private readonly NumericUpDown libraryBpmTo = new GlassNumericUpDown { Minimum = 30, Maximum = 300, DecimalPlaces = 2, Value = 300, Increment = 1 };
    private bool editingLibraryFilters;

    private void InitializeLibraryFilters()
    {
        libraryBpmBar.Radius = 9; browser.Controls.Add(libraryBpmBar);
        AddLabel(libraryBpmBar, "Sort", 12, 8, 36, 24, 9, PlayerTheme.Muted);
        libraryBpmSort.DropDownStyle = ComboBoxStyle.DropDownList; libraryBpmSort.FlatStyle = FlatStyle.Flat;
        libraryBpmSort.BackColor = PlayerTheme.Glass; libraryBpmSort.ForeColor = PlayerTheme.Text; libraryBpmSort.Font = new Font("Segoe UI", 9);
        libraryBpmSort.Items.AddRange(new object[] { "Default order", "BPM ascending", "BPM descending" }); libraryBpmSort.SelectedIndex = 0;
        libraryBpmSort.Bounds = new Rectangle(54, 6, 172, 30); libraryBpmSort.AccessibleName = "Library song sort order"; libraryBpmBar.Controls.Add(libraryBpmSort);
        libraryBpmRange.Text = "BPM range"; libraryBpmRange.ForeColor = PlayerTheme.Text; libraryBpmRange.BackColor = PlayerTheme.Glass; libraryBpmRange.Font = new Font("Segoe UI", 9);
        libraryBpmRange.Bounds = new Rectangle(246, 8, 108, 24); libraryBpmRange.AccessibleName = "Filter library by BPM range"; libraryBpmBar.Controls.Add(libraryBpmRange);
        NumericUpDown[] endpoints = { libraryBpmFrom, libraryBpmTo };
        for (int index = 0; index < endpoints.Length; index++)
        {
            NumericUpDown endpoint = endpoints[index]; endpoint.Font = new Font("Segoe UI", 9); endpoint.BackColor = PlayerTheme.Glass; endpoint.ForeColor = PlayerTheme.Text;
            endpoint.TextAlign = HorizontalAlignment.Center; endpoint.Enabled = false;
            GlassPanel frame = new GlassPanel { Radius = 9, Bounds = new Rectangle(index == 0 ? 364 : 482, 6, 84, 30), TabStop = false };
            endpoint.Bounds = new Rectangle(1, 4, 82, 22); endpoint.AccessibleName = index == 0 ? "Minimum library BPM" : "Maximum library BPM";
            endpoint.Top = (frame.Height - endpoint.Height) / 2;
            frame.Controls.Add(endpoint); libraryBpmBar.Controls.Add(frame);
            endpoint.Enter += delegate { frame.Outline = PlayerTheme.Purple; frame.Invalidate(); };
            endpoint.Leave += delegate { frame.Outline = PlayerTheme.Border; frame.Invalidate(); };
            endpoint.ValueChanged += delegate { ChangeLibraryBpmRange(endpoint); };
        }
        AddLabel(libraryBpmBar, "to", 452, 8, 26, 24, 9, PlayerTheme.Muted);
        GlassButton reset = MakeButton("Reset", libraryBpmBar, 584, 5, 70, 30, null); reset.Font = new Font("Segoe UI", 8.5f); reset.AccessibleName = "Reset library BPM sorting and range";
        reset.Click += delegate
        {
            editingLibraryFilters = true;
            try { libraryBpmSort.SelectedIndex = 0; libraryBpmRange.Checked = false; libraryBpmFrom.Value = 30; libraryBpmTo.Value = 300; }
            finally { editingLibraryFilters = false; }
            ApplyLibraryFilters();
        };
        libraryBpmSort.SelectedIndexChanged += delegate { if (!editingLibraryFilters) ApplyLibraryFilters(); };
        libraryBpmRange.CheckedChanged += delegate { if (!editingLibraryFilters) ApplyLibraryFilters(); };
        SetupButton(libraryGridView, "Grid", libraryBpmBar, "grid"); libraryGridView.Toggle = true; libraryGridView.Selected = true;
        SetupButton(libraryListView, "List", libraryBpmBar, "list"); libraryListView.Toggle = true;
        libraryGridView.AccessibleName = "Show library as a grid"; libraryListView.AccessibleName = "Show library as a list";
        libraryGridView.Click += delegate { ChangeLibraryView(false); };
        libraryListView.Click += delegate { ChangeLibraryView(true); };
        tip.SetToolTip(libraryGridView, "Show cover cards"); tip.SetToolTip(libraryListView, "Show compact song rows");
        tip.SetToolTip(libraryBpmSort, "Sort song cards by BPM. Songs with unknown BPM appear last.");
        tip.SetToolTip(libraryBpmRange, "Show songs within the selected BPM range, including both endpoints. Unknown BPM is excluded while the range is enabled.");
    }
    private void ChangeLibraryView(bool list)
    {
        if (artistsView && artistFolder == null) return;
        if (libraryListMode == list) return;
        libraryListMode = list; RenderBrowser(true);
    }
    private void ChangeLibraryBpmRange(NumericUpDown changed)
    {
        if (editingLibraryFilters) return;
        editingLibraryFilters = true;
        try
        {
            if (libraryBpmFrom.Value > libraryBpmTo.Value)
            {
                if (changed == libraryBpmFrom) libraryBpmTo.Value = libraryBpmFrom.Value;
                else libraryBpmFrom.Value = libraryBpmTo.Value;
            }
        }
        finally { editingLibraryFilters = false; }
        ApplyLibraryFilters();
    }
    private void ApplyLibraryFilters()
    {
        libraryBpmFrom.Enabled = libraryBpmTo.Enabled = libraryBpmRange.Checked;
        RenderBrowser(true);
    }
    private static bool HasLibraryBpm(SongInfo song) { return song.Bpm.HasValue && song.Bpm >= 30 && song.Bpm <= 300; }
    private List<SongInfo> MatchingLibrarySongs()
    {
        string query = librarySearch.Text.Trim();
        IEnumerable<SongInfo> matching = songs.Where(song => (artistFolder == null || String.Equals(ArtistDirectory(song), artistFolder, StringComparison.CurrentCultureIgnoreCase)) &&
            (query.Length == 0 || (song.ToString() + " " + ArtistDirectory(song)).IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0));
        if (libraryBpmRange.Checked) matching = matching.Where(song => HasLibraryBpm(song) && song.Bpm >= (double)libraryBpmFrom.Value && song.Bpm <= (double)libraryBpmTo.Value);
        if (libraryBpmSort.SelectedIndex == 1) matching = matching.OrderBy(song => !HasLibraryBpm(song)).ThenBy(song => HasLibraryBpm(song) ? song.Bpm.Value : 0).ThenBy(song => song.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(song => song.Artist, StringComparer.CurrentCultureIgnoreCase);
        else if (libraryBpmSort.SelectedIndex == 2) matching = matching.OrderBy(song => !HasLibraryBpm(song)).ThenByDescending(song => HasLibraryBpm(song) ? song.Bpm.Value : 0).ThenBy(song => song.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(song => song.Artist, StringComparer.CurrentCultureIgnoreCase);
        return matching.ToList();
    }
}
