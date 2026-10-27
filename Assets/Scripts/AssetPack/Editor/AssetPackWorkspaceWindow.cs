using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetPack.Common;
using AssetPack.Common.Dependencies;
using UnityEditor;
using UnityEngine;
using DataArtifact = AssetPack.Editor.AssetPackSyncHelper.DataArtifact;
using SyncDirection = AssetPack.Editor.AssetPackSyncHelper.SyncDirection;

namespace AssetPack.Editor
{
    /// <summary>
    /// Single-window Asset Pack workspace covering the whole modder workflow. (Maintainer-only distribution
    /// tooling lives separately in <see cref="AssetPackDistributionWindow"/>, backed by
    /// <see cref="AssetPackDistribution"/>.)
    ///
    /// A pack can live in up to three places, and every action moves it between them:
    ///   ModDev  &lt;-&gt;  ADK  &lt;-&gt;  Storage
    ///   (runs)       (edit)      (keep)
    ///
    ///   Storage = the durable library you keep packs in       (AssetPackEditorPrefs.LibraryPath)
    ///   ADK     = your editable working copy inside this project (AssetPackEditorStore.LocalBasePath)
    ///   ModDev  = what the running game loads                  (AssetPackEditorPrefs.BuildPath)
    ///
    /// Verbs: Load from Storage (Storage-&gt;ADK), Build (ADK-&gt;ModDev, full bundle rebuild), Push Text
    /// (ADK-&gt;ModDev, forced text-only fast path, no rebuild), Pull ModDev Edits (ModDev-&gt;ADK),
    /// Save to Storage (ADK-&gt;Storage), Discard (delete the ADK copy).
    ///
    /// A fourth read-only class, Shared reference packs (AssetPackEditorStore.SharedPacksBasePath), is
    /// browsable under its own tab: compiled into builds for dependency resolution, never editable here.
    ///
    /// Depends only on the sibling AssetPack.Editor / AssetPack.Common backend, so it copies into the
    /// ADK unchanged.
    /// </summary>
    public class AssetPackWorkspaceWindow : EditorWindow
    {
        private static string StoragePath => AssetPackEditorPrefs.LibraryPath;
        private static string ModDevPath => AssetPackEditorPrefs.BuildPath;
        private static string AdkPath => AssetPackEditorStore.LocalBasePath;
        private static string SharedPath => AssetPackEditorStore.SharedPacksBasePath;

        private enum Tab { Adk, Storage, Shared }

        [SerializeField] private Tab _tab = Tab.Adk;
        [SerializeField] private string _selectedDir;   // null => list view; otherwise drill-down detail
        [SerializeField] private string _filter = "";

        private Vector2 _listScroll;
        private Vector2 _detailScroll;
        private string _status = "Ready.";
        private bool _showPaths;
        private bool _showCreate;

        // create-pack scratch
        private string _newDomain = "";
        private string _newPackName = "";

        // detail editing scratch
        private string _newRequired = "";
        private string _newOptional = "";
        private string _newIncompatible = "";
        private bool _foldMeta = true;
        private bool _foldDeps;
        private bool _foldExports;
        private bool _foldSyncDetail = true;
        private bool _foldAssets = true;

        // In-memory edit state for the open pack. Metadata + dependencies mutate the loaded store's Info
        // directly; exports edit this scratch list (so an in-progress key/path retype never trips the
        // validate-on-mutate backend). All of it commits together on Save (CommitDetail).
        private readonly List<ExportRow> _exportRows = new List<ExportRow>();
        private bool _detailDirty;

        // cached view model
        private List<PackRow> _rows = new List<PackRow>();
        private AssetPackEditorStore _selStore;
        private AssetPackInfoEditor _selEditor;

        private static readonly Color OkColor = new Color(0.42f, 0.72f, 0.45f);
        private static readonly Color WarnColor = new Color(0.85f, 0.66f, 0.22f);
        private static readonly Color CritColor = new Color(0.82f, 0.37f, 0.30f);
        private static readonly Color OffColor = new Color(0.5f, 0.5f, 0.5f);
        // One accent, one meaning: "this is the action to take". Anything passive (a state badge, a
        // direction label) uses the neutral/warn tones instead, so the accent never reads as clickable
        // where nothing is.
        private static readonly Color Accent = new Color(0.55f, 0.72f, 0.95f);

        private class ExportRow
        {
            public string Key = "";
            public string Path = "";
        }

        private class PackRow
        {
            public string Dir;
            public string Identifier;
            public string Name;
            public string Version;
            public bool InAdk;
            public bool InStorage;
            public bool InModDev;
            public bool InShared;    // read-only reference pack
            public AssetPackSyncHelper.SyncDirection Sync;
            // How many files ModDev has that the ADK lacks. Separate from Sync because the two causes
            // need different advice and the comparison cannot tell them apart: a map authored
            // in-game, or a file deleted in the ADK.
            public int ModDevOnlyFiles;
            // Per-file breakdown behind Sync, computed once per Refresh. The detail view
            // reads it rather than re-scanning: OnGUI runs on every mouse move, and these hit the disk.
            public (string Label, AssetPackSyncHelper.SyncStatus Status)[] ArtifactChecks;
            public bool HasDefinitions; // pack ships a Definitions.json, edited in-game, not here
        }

        [MenuItem("Railroader/Asset Packs")]
        internal static void ShowWindow()
        {
            var window = GetWindow<AssetPackWorkspaceWindow>();
            window.titleContent = new GUIContent("Asset Packs");
            window.minSize = new Vector2(360, 420);
            window.Show();
        }

        private void OnEnable()
        {
            // Rows highlight under the cursor, which only tracks if the window sees mouse movement.
            wantsMouseMove = true;
            Refresh(true);
        }
        private void OnFocus() => Refresh();

        // Best-effort: don't lose valid edits when the window closes or a domain reload tears it down.
        // Invalid edits (which would write a broken Info.json) are dropped instead.
        private void OnDisable()
        {
            if (_detailDirty && _selStore != null && CollectDetailErrors().Count == 0)
                ApplyDetail();
        }

        // ------------------------------------------------------------------ model

        // Rebuilds the pack list from disk. Pass reloadSelected: true ONLY when the selected pack's
        // on-disk content or location changed (Load / Pull / Push / Save / Build / Create). Otherwise the
        // in-memory _selStore is kept, so unsaved metadata/dependency/export edits survive focus changes
        // and repaints (e.g. tabbing out to drag assets into the pack folder and back).
        private void Refresh(bool reloadSelected = false)
        {
            _rows = BuildRows();

            if (reloadSelected)
            {
                _selStore = null;
                _selEditor = null;
            }
            EnsureSelectionLoaded();
        }

        private void EnsureSelectionLoaded()
        {
            if (_selectedDir == null)
            {
                _selStore = null;
                _selEditor = null;
                return;
            }

            var row = _rows.FirstOrDefault(r => r.Dir == _selectedDir);
            if (row == null)
            {
                // selection vanished (discarded, renamed); fall back to the list
                _selectedDir = null;
                _selStore = null;
                _selEditor = null;
                return;
            }

            // Already holding this pack in memory? Keep it so unsaved edits aren't discarded.
            if (_selStore != null && _selStore.DirectoryName == _selectedDir)
                return;

            // Prefer the ADK copy for editing; fall back to Storage, then Shared, for read-only display.
            var basePath = row.InAdk ? AdkPath : row.InStorage ? StoragePath : SharedPath;
            try
            {
                _selStore = AssetPackEditorStore.Load(basePath, _selectedDir, isReferencePack: !row.InAdk && !row.InStorage);
                _selEditor = new AssetPackInfoEditor(_selStore.GetInfo());
                RebuildExportScratch();
                _detailDirty = false;
            }
            catch (Exception e)
            {
                _selStore = null;
                _selEditor = null;
                SetStatus($"Failed to load {_selectedDir}: {e.Message}");
            }
        }

        private void RebuildExportScratch()
        {
            _exportRows.Clear();
            if (_selEditor == null)
                return;
            foreach (var kvp in _selEditor.GetExports())
                _exportRows.Add(new ExportRow { Key = kvp.Key, Path = kvp.Value });
        }

        private static List<PackRow> BuildRows()
        {
            var adkDirs = AssetPackEditorStore.FindAllStores(AdkPath).ToHashSet();
            var storageDirs = (!string.IsNullOrEmpty(StoragePath) && Directory.Exists(StoragePath))
                ? AssetPackEditorStore.FindAllStores(StoragePath).ToHashSet()
                : new HashSet<string>();
            var sharedDirs = Directory.Exists(SharedPath)
                ? AssetPackEditorStore.FindAllStores(SharedPath).ToHashSet()
                : new HashSet<string>();

            var rows = new List<PackRow>();
            foreach (var dir in adkDirs.Union(storageDirs).Union(sharedDirs)
                         .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var row = new PackRow
                {
                    Dir = dir,
                    InAdk = adkDirs.Contains(dir),
                    InStorage = storageDirs.Contains(dir),
                    InShared = sharedDirs.Contains(dir),
                    InModDev = !string.IsNullOrEmpty(ModDevPath) && Directory.Exists(Path.Combine(ModDevPath, dir)),
                };

                var infoBase = row.InAdk ? AdkPath : row.InStorage ? StoragePath : SharedPath;
                row.HasDefinitions = File.Exists(Path.Combine(infoBase, dir, Filenames.Definitions));
                try
                {
                    var info = AssetPackEditorStore.Load(infoBase, dir).GetInfo();
                    row.Identifier = string.IsNullOrEmpty(info.Identifier) ? dir : info.Identifier;
                    row.Name = info.Name;
                    row.Version = info.Version;
                }
                catch
                {
                    row.Identifier = dir;
                    row.Name = "(unreadable Info.json)";
                }

                if (row.InAdk && row.InModDev)
                {
                    // BuildRows runs from OnFocus, and the sweep throws on an unreadable subdirectory:
                    // an escaping exception would stop the window drawing at all.
                    try
                    {
                        row.ArtifactChecks = ArtifactSyncChecks(dir);
                    }
                    catch (Exception e)
                    {
                        row.ArtifactChecks = Array.Empty<(string, AssetPackSyncHelper.SyncStatus)>();
                        Debug.LogWarning($"{dir}: could not compare ModDev and the ADK: {e.Message}");
                    }

                    foreach (var (_, status) in row.ArtifactChecks)
                    {
                        row.Sync = Combined(row.Sync, status.Direction);
                        row.ModDevOnlyFiles += status.ModDevOnlyFiles;
                    }
                }

                rows.Add(row);
            }
            return rows;
        }

        /// <summary>
        /// Rolls one artifact's direction into the row's. Two artifacts ahead on opposite sides
        /// leave the row diverged, the same as one artifact that is diverged by itself: either way
        /// there is no direction to offer, and the row must not read as in sync.
        /// </summary>
        private static SyncDirection Combined(SyncDirection row, SyncDirection artifact)
        {
            if (artifact is SyncDirection.Absent or SyncDirection.InSync)
                return row;
            if (row is SyncDirection.Absent or SyncDirection.InSync)
                return artifact;
            return row == artifact ? row : SyncDirection.Diverged;
        }

        /// <summary>
        /// The artifacts Push Text and Pull move between ADK and ModDev, each compared
        /// individually. The rolled-up state on the pack row says only *that* the two copies differ;
        /// these are what say which file, in which direction, and by how much.
        /// </summary>
        private static (string Label, AssetPackSyncHelper.SyncStatus Status)[] ArtifactSyncChecks(
            string dir)
        {
            var modDev = Path.Combine(ModDevPath, dir);
            var adk = Path.Combine(AdkPath, dir);

            return new[]
            {
                ("Info.json", AssetPackSyncHelper.CompareSingleFile(
                    Path.Combine(modDev, Filenames.Info), Path.Combine(adk, Filenames.Info), "Info.json")),
                ("Definitions", AssetPackSyncHelper.CompareSingleFile(
                    Path.Combine(modDev, Filenames.Definitions), Path.Combine(adk, Filenames.Definitions), "definitions")),
                ("Data Files", AssetPackSyncHelper.ComparePayload(modDev, adk)),
            };
        }

        // ------------------------------------------------------------------ GUI

        private void OnGUI()
        {
            // wantsMouseMove delivers the movement; the row highlight only tracks the cursor if we also
            // ask for the repaint that redraws it.
            if (Event.current.type == EventType.MouseMove)
                Repaint();

            DrawToolbar();
            if (_showPaths) DrawPaths();

            if (_selectedDir == null)
                DrawList();
            else
                DrawDetail();

            DrawFooter();
            DrawStatusBar();
        }

        private void DrawFooter()
        {
            // List view only: in the detail view this sits below a "Build" that means *this pack*, and
            // the two are indistinguishable at a glance.
            if (_selectedDir != null)
                return;
            if (!_rows.Any(r => r.InAdk))
                return;

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Build ADK Packs",
                        "Build every pack in the ADK, in dependency order, into ModDev."),
                        GUILayout.Height(22), GUILayout.Width(150)))
                    BuildAllAdk();
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Asset Packs", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("+ New", EditorStyles.toolbarButton))
                {
                    if (TryLeaveDetail())
                    {
                        _showCreate = !_showCreate;
                        _tab = Tab.Adk;
                        _selectedDir = null;
                    }
                }
                _showPaths = GUILayout.Toggle(_showPaths, "Paths", EditorStyles.toolbarButton);
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
                    Refresh();
            }
        }

        private void DrawPaths()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawPathRow("Storage", StoragePath, p => AssetPackEditorPrefs.LibraryPath = p);
                // Unset and missing are different problems: the first needs configuring, the second
                // usually means a disconnected drive or a moved checkout.
                if (string.IsNullOrEmpty(StoragePath))
                    EditorGUILayout.HelpBox(
                        "No Storage path set. Set one to load packs from, or keep working ADK-only.",
                        MessageType.Info);
                else if (!Directory.Exists(StoragePath))
                    EditorGUILayout.HelpBox(
                        "Storage path is set but doesn't exist on disk. The drive may be disconnected, " +
                        "or the folder moved.", MessageType.Warning);
                DrawPathRow("ModDev", ModDevPath, p => AssetPackEditorPrefs.BuildPath = p);
                EditorGUILayout.LabelField("ADK", AdkPath, EditorStyles.miniLabel);
            }
        }
        private void DrawPathRow(string label, string value, Action<string> set)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(60));
                EditorGUILayout.SelectableLabel(string.IsNullOrEmpty(value) ? "(not set)" : value,
                    EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("Change", GUILayout.Width(70)))
                {
                    var picked = EditorUtility.OpenFolderPanel($"Select {label} path", value ?? "", "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        set(picked);
                        Refresh();
                    }
                }
            }
        }

        // ------------------------------------------------------------------ list

        private void DrawList()
        {
            var adkCount = _rows.Count(r => r.InAdk);
            var storageCount = _rows.Count(r => r.InStorage);

            using (new EditorGUILayout.HorizontalScope())
            {
                // Shared is a stable reference library; its count isn't actionable, so it's left off to
                // keep three tabs legible at ~360px.
                var newTab = (Tab)GUILayout.Toolbar((int)_tab,
                    new[] { $"ADK ({adkCount})", $"Storage ({storageCount})", "Shared" });
                if (newTab != _tab)
                {
                    _tab = newTab;
                    _filter = "";
                }
            }

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                _filter = GUILayout.TextField(_filter, GUI.skin.FindStyle("ToolbarSearchTextField") ?? EditorStyles.toolbarTextField);
                if (GUILayout.Button("", GUI.skin.FindStyle("ToolbarSearchCancelButton") ?? EditorStyles.toolbarButton))
                {
                    _filter = "";
                    GUI.FocusControl(null);
                }
            }

            if (_tab == Tab.Adk && _showCreate)
                DrawCreatePanel();

            var shown = _rows
                .Where(r => _tab == Tab.Adk ? r.InAdk : _tab == Tab.Storage ? r.InStorage : r.InShared)
                // Directory name is matched too: it can diverge from the identifier (nothing keeps them
                // in step), and the folder name is often what you know a pack by.
                .Where(r => string.IsNullOrEmpty(_filter)
                            || r.Identifier.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || (r.Name ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
                            || r.Dir.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
            if (shown.Count == 0)
            {
                EditorGUILayout.LabelField(EmptyListMessage(), EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                DrawGlyphLegend();
                foreach (var row in shown)
                    DrawPackRow(row);
            }
            EditorGUILayout.EndScrollView();
        }

        private string EmptyListMessage()
        {
            switch (_tab)
            {
                case Tab.Adk:
                    return "Nothing loaded. Open the Storage tab to load a pack into the ADK.";
                case Tab.Shared:
                    return "No shared reference packs.";
                default:
                    return string.IsNullOrEmpty(StoragePath)
                        ? "Set a Storage path (Paths button) to browse your library."
                        : "No packs match.";
            }
        }

        private void DrawCreatePanel()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Create Asset Pack", EditorStyles.boldLabel);

                // Snapshotted before the fields are drawn: deciding whether to draw a HelpBox from a
                // value the same event mutates changes the control count mid-event, which IMGUI throws
                // on. The messages lag one repaint behind the keystroke, which is invisible.
                var domainErr = ValidateIdentifierPart(_newDomain, "Domain");
                var nameErr = ValidateIdentifierPart(_newPackName, "Pack Name");
                var named = !string.IsNullOrEmpty(_newDomain) && !string.IsNullOrEmpty(_newPackName);
                // The folder is named after the pack name alone, so the domain does not keep two packs
                // of the same name apart on disk.
                var collision = nameErr == null && !string.IsNullOrEmpty(_newPackName)
                    && Directory.Exists(Path.Combine(AdkPath, _newPackName));

                _newDomain = EditorGUILayout.TextField(new GUIContent("Domain", "Your author domain, e.g. your username"), _newDomain);
                _newPackName = EditorGUILayout.TextField(new GUIContent("Pack Name", "Combined as domain/pack-name"), _newPackName);

                if (domainErr != null) EditorGUILayout.HelpBox(domainErr, MessageType.Error);
                if (nameErr != null) EditorGUILayout.HelpBox(nameErr, MessageType.Error);
                if (collision)
                    EditorGUILayout.HelpBox(
                        $"Assets/AssetPacks/{_newPackName} already exists. The folder is named after the " +
                        "pack name alone, so this would land on top of that pack.", MessageType.Error);

                var valid = domainErr == null && nameErr == null && named && !collision;
                var identifier = valid ? $"{_newDomain}/{_newPackName}" : "";

                using (new EditorGUI.DisabledScope(!valid))
                {
                    if (GUILayout.Button(valid ? $"Create \"{identifier}\"" : "Create"))
                        CreatePack(_newPackName, identifier);
                }
            }
        }

        private const float PackRowHeight = 22f;
        private const float PackRowPadding = 4f;

        /// <summary>
        /// One pack in the list. Drawn into a single reserved rect rather than as a row of layout
        /// controls, so the whole row (glyph, name, identifier, status) is one hit target that
        /// highlights on hover, instead of only the name being clickable.
        /// </summary>
        private void DrawPackRow(PackRow row)
        {
            var rect = GUILayoutUtility.GetRect(0, PackRowHeight, GUILayout.ExpandWidth(true));
            var e = Event.current;
            var hover = rect.Contains(e.mousePosition);

            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                _selectedDir = row.Dir;
                _showCreate = false;
                Refresh();
                e.Use();
                GUIUtility.ExitGUI();
            }

            if (e.type == EventType.Repaint && hover)
                EditorGUI.DrawRect(rect, new Color(0.5f, 0.55f, 0.65f, 0.18f));

            var x = rect.x + PackRowPadding;
            DrawGlyph(new Rect(x, rect.y, GlyphWidth, rect.height), row);
            x += GlyphWidth + 8f;

            // Right-aligned status first, so the name knows how much room is left and elides into it
            // rather than over it.
            var status = RowStatus(row);
            var statusWidth = 0f;
            if (status.label != null)
            {
                var style = MiniColored(status.color);
                statusWidth = style.CalcSize(new GUIContent(status.label)).x + PackRowPadding;
                var statusRect = new Rect(rect.xMax - statusWidth, rect.y, statusWidth - PackRowPadding, rect.height);
                GUI.Label(statusRect, new GUIContent(status.label, status.tip), style);
            }

            // Name reads first, identifier as a muted caption, the same hierarchy as the detail view.
            var name = string.IsNullOrEmpty(row.Name) ? row.Identifier : row.Name;
            var nameWidth = PackRowStyle.CalcSize(new GUIContent(name)).x;
            var available = Mathf.Max(0f, rect.xMax - statusWidth - x);
            nameWidth = Mathf.Min(nameWidth, available);
            GUI.Label(new Rect(x, rect.y, nameWidth, rect.height), new GUIContent(name, row.Identifier), PackRowStyle);

            // The identifier is a caption: it takes whatever the name left over, and is dropped entirely
            // when the window is too narrow for both.
            var idX = x + nameWidth + 6f;
            var idWidth = rect.xMax - statusWidth - idX;
            if (idWidth > 24f)
                GUI.Label(new Rect(idX, rect.y, idWidth, rect.height), row.Identifier, MiniColored(OffColor));
        }

        // The right-hand status word for a row, per tab.
        private (string label, Color color, string tip) RowStatus(PackRow row)
        {
            if (_tab == Tab.Adk)
                return NeedVisual(NeedFor(row));
            if (_tab == Tab.Shared)
                return ("reference", OffColor, "Read-only reference pack; can't be edited here.");
            return row.InAdk ? ("loaded", OkColor, "Already loaded into the ADK.") : (null, default, null);
        }

        // Width the pack row reserves for the flow glyph: three cells joined by two connectors.
        private const float GlyphCell = 7f, GlyphGap = 5f;
        private const float GlyphWidth = GlyphCell * 3f + GlyphGap * 2f;

        /// <summary>
        /// The ModDev -- ADK -- Storage pipeline as three linked cells: a miniature of the location
        /// diagram in the detail view, so the same picture means the same thing in both places.
        ///
        /// Presence is carried by fill (solid = the pack is there, hollow = it isn't), not by hue alone.
        /// Color then only grades *how* it is there, and the row still reads without color vision.
        /// Connectors between the cells are what make it a flow rather than three unrelated dots.
        /// </summary>
        private void DrawGlyph(Rect r, PackRow row)
        {
            var y = r.y + (r.height - GlyphCell) / 2f;
            var midY = y + GlyphCell / 2f - 0.5f;
            var connector = new Color(0.5f, 0.5f, 0.5f, 0.5f);

            // Connectors sit in the gaps only, so a hollow cell stays legibly empty.
            for (var i = 0; i < 2; i++)
            {
                var x = r.x + GlyphCell * (i + 1) + GlyphGap * i;
                EditorGUI.DrawRect(new Rect(x, midY, GlyphGap, 1f), connector);
            }

            DrawGlyphCell(new Rect(r.x, y, GlyphCell, GlyphCell), row.InModDev, ToneModDev(row));
            DrawGlyphCell(new Rect(r.x + GlyphCell + GlyphGap, y, GlyphCell, GlyphCell), row.InAdk, OkColor);
            DrawGlyphCell(new Rect(r.x + (GlyphCell + GlyphGap) * 2f, y, GlyphCell, GlyphCell), row.InStorage,
                row.InStorage ? OkColor : (row.InAdk ? WarnColor : OffColor));

            // Hover is not the only way to learn this (the list header spells the stages out), but the
            // per-row state has to be readable somehow other than by decoding three small squares.
            GUI.Label(r, new GUIContent(string.Empty, GlyphTooltip(row)));
        }

        private static void DrawGlyphCell(Rect r, bool present, Color color)
        {
            if (present)
            {
                EditorGUI.DrawRect(r, color);
                return;
            }
            // Hollow: a faint outline. Absence should read as an empty slot in the pipeline, not as a
            // second kind of filled cell.
            DrawFrame(r, new Color(OffColor.r, OffColor.g, OffColor.b, 0.5f));
        }

        private static string GlyphTooltip(PackRow row)
        {
            var modDev = !row.InModDev ? "not built"
                : row.Sync switch
                {
                    SyncDirection.Diverged => "diverged",
                    SyncDirection.ModDevAhead => "live edits",
                    SyncDirection.AdkAhead => "behind the ADK",
                    _ => "running"
                };
            var adk = row.InAdk ? "loaded" : "not loaded";
            var storage = row.InStorage ? "saved" : "not saved";
            return $"ModDev: {modDev}\nADK: {adk}\nStorage: {storage}";
        }

        // Names the glyph's three stages once, above the list, so the mapping is learnable without hover.
        private void DrawGlyphLegend()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var rect = GUILayoutUtility.GetRect(GlyphWidth, EditorGUIUtility.singleLineHeight,
                    GUILayout.Width(GlyphWidth));
                var legendRow = new PackRow { InModDev = true, InAdk = true, InStorage = true };
                DrawGlyph(rect, legendRow);
                GUILayout.Label("ModDev - ADK - Storage", MiniColored(OffColor));
                GUILayout.FlexibleSpace();
            }
        }

        private static Color ToneModDev(PackRow row)
        {
            if (!row.InModDev) return OffColor;
            if (row.Sync is SyncDirection.Absent or SyncDirection.InSync) return OkColor;
            return WarnColor;
        }

        private enum Need { None, Build, PushText, Pull, Save }

        // The one most-urgent action an ADK row needs, or None when it's clean.
        // Order matters: unpulled ModDev edits (Pull) outrank everything, because they exist in only
        // one place - steering a diverged pack to Build/Push first would overwrite them. Pull is not
        // itself safe on a diverged pack (it copies every artifact back); ConfirmReplacingNewer
        // is what names whatever the ADK would lose to it.
        private static Need NeedFor(PackRow row)
        {
            if (!row.InAdk) return Need.None;
            // Diverged too: ModDev holds edits either way, and they exist in only one place.
            if (row.Sync is SyncDirection.ModDevAhead or SyncDirection.Diverged) return Need.Pull;
            if (!row.InModDev) return Need.Build;     // never built -> must create bundles first
            if (row.Sync == SyncDirection.AdkAhead) return Need.PushText; // the fast path is the common case
            if (!row.InStorage) return Need.Save;
            return Need.None;
        }

        // Phrased as a state (what the pack currently is), not a command - it's a status flag, not a button.
        private static (string label, Color color, string tip) NeedVisual(Need need) => need switch
        {
            Need.Build => ("needs build", WarnColor, "Not yet built into ModDev - Build to load it in the game"),
            Need.PushText => ("unpushed edits", WarnColor, "ADK text is ahead of ModDev - Push Text to update the running game"),
            Need.Pull => ("ModDev edits", WarnColor, "You edited files in ModDev while testing - Pull them into the ADK"),
            Need.Save => ("unsaved", WarnColor, "Not yet saved to Storage"),
            _ => (null, default, null),
        };

        // ------------------------------------------------------------------ detail

        private void DrawDetail()
        {
            var row = _rows.FirstOrDefault(r => r.Dir == _selectedDir);
            if (row == null || _selStore == null)
            {
                _selectedDir = null;
                // ExitGUI throws, so the return is unreachable -- but row is dereferenced below,
                // and nothing at that line says the throw is what protects it.
                GUIUtility.ExitGUI();
                return;
            }

            var editable = row.InAdk;

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("< Packs", EditorStyles.toolbarButton, GUILayout.Width(70)))
                {
                    if (TryLeaveDetail())
                    {
                        _selectedDir = null;
                        GUIUtility.ExitGUI();
                    }
                }
                GUILayout.FlexibleSpace();
                if (editable)
                {
                    // Apply/Revert, not Save/Revert: this writes the form's edits to the ADK copy of
                    // Info.json, which is a subroutine of every action below (Build, Push Text, Save to
                    // Storage all commit first). "Save" belongs to the Storage action, which is what the
                    // location diagram and the row badges mean by "saved". Disabled when clean, so the
                    // button can't report work it didn't do. That also carries the dirty state without
                    // the old "Save*" asterisk.
                    using (new EditorGUI.DisabledScope(!_detailDirty))
                    {
                        if (GUILayout.Button(new GUIContent("Revert", "Discard these edits and reload Info.json from disk."),
                                EditorStyles.toolbarButton, GUILayout.Width(56)))
                            ReloadSelection();

                        var prev = GUI.backgroundColor;
                        if (_detailDirty) GUI.backgroundColor = Accent;
                        if (GUILayout.Button(new GUIContent("Apply",
                                "Write these Info.json edits to the ADK copy. Edits are applied automatically when you leave this pack or run an action."),
                                EditorStyles.toolbarButton, GUILayout.Width(56)))
                            CommitDetail();
                        GUI.backgroundColor = prev;
                    }
                }
            }

            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll);

            // Keep the prefix-label column narrow so metadata fields fit a ~450px window.
            EditorGUIUtility.labelWidth = 100;

            var info = _selStore.GetInfo();
            GUILayout.Label(string.IsNullOrEmpty(info.Name) ? row.Identifier : info.Name, EditorStyles.boldLabel);
            GUILayout.Label($"{row.Identifier}   v{info.Version}", EditorStyles.miniLabel);

            var sharedOnly = row.InShared && !row.InAdk && !row.InStorage;
            if (sharedOnly)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox(
                    "Reference pack, read-only. Depend on it by identifier; it can't be edited or built here.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.Space(4);
                DrawLocationBlocks(row);

                EditorGUILayout.Space(4);
                DrawActions(row);

                DrawTextSyncDetail(row);
                DrawDefinitionsCallout(row);
            }

            EditorGUILayout.Space(6);
            DrawMetadata(editable);
            DrawDependencies(editable);
            DrawExports(editable);
            DrawAssets(row);

            EditorGUILayout.EndScrollView();
        }

        private const string ModDevTooltip =
            "ModDev - what the running game loads.\nBuild copies this pack here so you can test it in the game.";
        private const string AdkTooltip =
            "ADK - your editable working copy in this Unity project.\nEdit the pack here, then Build to test it or Save to Storage to keep it.";
        private const string StorageTooltip =
            "Storage - your durable pack library.\nLoad from Storage to start editing; Save to Storage to keep your changes.";

        // A small pipeline diagram: three centered cells joined by dashes, so it reads as the linear
        // ModDev -- ADK -- Storage relationship rather than three separate buttons.
        // Drawn with manual rects (not GUILayout) so the dashes land exactly at the cells' vertical
        // center. Reserves one row, centers the 3-cell + 2-dash group in it, positions each piece.
        private void DrawLocationBlocks(PackRow row)
        {
            const float cellW = 84f, cellH = 34f, dashW = 22f;
            var total = cellW * 3f + dashW * 2f;

            var area = GUILayoutUtility.GetRect(10f, cellH, GUILayout.ExpandWidth(true));
            var x = area.x + Mathf.Max(0f, (area.width - total) * 0.5f);

            DrawLocCell(new Rect(x, area.y, cellW, cellH), "ModDev", ModDevStatus(row), ModDevTooltip);
            x += cellW;
            GUI.Label(new Rect(x, area.y, dashW, cellH), "--", DashStyle);
            x += dashW;
            DrawLocCell(new Rect(x, area.y, cellW, cellH), "ADK", row.InAdk ? ("Loaded", OkColor) : ("Not loaded", OffColor), AdkTooltip);
            x += cellW;
            GUI.Label(new Rect(x, area.y, dashW, cellH), "--", DashStyle);
            x += dashW;
            DrawLocCell(new Rect(x, area.y, cellW, cellH), "Storage", row.InStorage ? ("Saved", OkColor) : ("Not saved", WarnColor), StorageTooltip);
        }

        /// <summary>
        /// Points at the in-game Definition Editor when the pack has definitions to edit. The field
        /// values in Definitions.json are validated, cross-referenced and previewed in-game in a way
        /// this window can't reproduce, so the useful thing to say here is "not here, there".
        /// </summary>
        private void DrawDefinitionsCallout(PackRow row)
        {
            if (!row.HasDefinitions || !row.InAdk) return;

            var next = row.InModDev
                ? "Edit them in-game with the Definition Editor, then Pull to bring the changes back here."
                : "Build this pack first, then edit them in-game with the Definition Editor and Pull the changes back here.";

            EditorGUILayout.HelpBox($"This pack has a Definitions.json. {next}", MessageType.Info);
        }

        /// <summary>
        /// Per-file breakdown of the ADK/ModDev comparison, shown only once the pack is in both places
        /// and the two actually differ. When they agree, the rolled-up "Up to date" state says everything.
        /// </summary>
        private void DrawTextSyncDetail(PackRow row)
        {
            if (!row.InAdk || !row.InModDev || row.ArtifactChecks == null) return;
            if (row.Sync is SyncDirection.Absent or SyncDirection.InSync) return;

            _foldSyncDetail = EditorGUILayout.Foldout(_foldSyncDetail, "Artifact differences", true);
            if (!_foldSyncDetail) return;

            EditorGUI.indentLevel++;
            foreach (var (label, status) in row.ArtifactChecks)
            {
                var color = status.Direction is SyncDirection.Absent or SyncDirection.InSync
                    ? OffColor
                    : WarnColor;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(90));
                    EditorGUILayout.LabelField(status.Message, MiniWrapped(color));
                }
            }
            GUILayout.Label("Pull brings ModDev edits back; Push Text sends the ADK copy out.",
                EditorStyles.miniLabel);
            EditorGUI.indentLevel--;
        }

        private static (string, Color) ModDevStatus(PackRow row)
        {
            if (!row.InModDev) return ("Not built", OffColor);
            return row.Sync switch
            {
                SyncDirection.Diverged => ("Diverged", CritColor),
                SyncDirection.ModDevAhead => ("Live edits", WarnColor),
                SyncDirection.AdkAhead => ("Behind ADK", WarnColor),
                _ => ("Up to date", OkColor)
            };
        }

        private void DrawLocCell(Rect r, string name, (string label, Color color) status, string tooltip)
        {
            // A faint outline, not a Box: these are read-outs of where the pack is, and a helpBox frame
            // makes them read as three buttons sitting above the row of actual buttons.
            DrawFrame(r, new Color(0.5f, 0.5f, 0.5f, 0.22f));
            GUI.Label(new Rect(r.x, r.y + 5f, r.width, 14f), name, CellNameStyle);
            GUI.Label(new Rect(r.x, r.yMax - 16f, r.width, 13f), status.label, CellStatusStyle(status.color));
            // Transparent overlay over the whole cell carries the tooltip on hover.
            GUI.Label(r, new GUIContent(string.Empty, tooltip));
        }

        [NonSerialized] private GUIStyle _packRowStyle;
        // The pack row is a button wearing a label's clothes. Without a hover state it reads as static
        // text, so the accent on hover is the only thing saying "this is clickable".
        private GUIStyle PackRowStyle => _packRowStyle ??= new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip,
        };

        // 1px outline, no fill.
        private static void DrawFrame(Rect r, Color color)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1f), color);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1f, r.width, 1f), color);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1f, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - 1f, r.y, 1f, r.height), color);
        }

        [NonSerialized] private GUIStyle _cellNameStyle;
        private GUIStyle CellNameStyle => _cellNameStyle ??=
            new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };

        [NonSerialized] private GUIStyle _dashStyle;
        private GUIStyle DashStyle => _dashStyle ??= new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            margin = new RectOffset(2, 2, 0, 0),
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f, 0.55f) }
        };

        private readonly Dictionary<Color, GUIStyle> _centeredColoredStyles = new Dictionary<Color, GUIStyle>();
        private GUIStyle CellStatusStyle(Color color)
        {
            if (!_centeredColoredStyles.TryGetValue(color, out var style))
            {
                style = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
                style.normal.textColor = color;
                _centeredColoredStyles[color] = style;
            }
            return style;
        }

        private void DrawActions(PackRow row)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (!row.InAdk)
                {
                    if (row.InStorage && Button("Load from Storage",
                            "Copy this pack from Storage into the ADK so you can edit it.", true))
                        LoadFromStorage(row.Dir);
                }
                else
                {
                    var need = NeedFor(row);

                    // Build: full bundle rebuild. Primary only when the pack has never been built (there
                    // are no bundles yet); otherwise it stays available but neutral.
                    if (Button("Build",
                            "Rebuild asset bundles and reproduce this pack in ModDev: overwrites what is there " +
                            "and removes Data/Terrain files the ADK doesn't have. Asks first if that would lose " +
                            "work you authored in-game.",
                            need == Need.Build))
                        Build();

                    // Push Text: fast, forced copy of everything outside the bundle. Only meaningful once
                    // a build exists; the game hot-reloads these without a rebuild.
                    if (row.InModDev && Button("Push Text",
                            "Copy Definitions.json, scripts, Info.json and the Data/Terrain files to ModDev " +
                            "instantly. Adds and overwrites only - it never removes, so a file you deleted in " +
                            "the ADK stays until you Build. No bundle rebuild; reload in-game to see changes.",
                            need == Need.PushText))
                        PushTextToModDev(row.Dir);

                    // Pull: recover edits made in ModDev. Primary whenever ModDev has unpulled edits.
                    if (row.Sync is SyncDirection.ModDevAhead or SyncDirection.Diverged && Button("Pull",
                            "Copy the edits you made in ModDev back into the ADK.",
                            need == Need.Pull))
                        PullModDevEdits(row.Dir);

                    // Left group acts on the ModDev leg, right group on the Storage leg, in the same
                    // left-to-right order as the location diagram above. The space also pins Save in
                    // place while Push Text/Pull come and go, so it can't slide under the cursor.
                    GUILayout.FlexibleSpace();

                    if (Button("Save",
                            "Save this pack to your Storage library, replacing the copy that's there.",
                            need == Need.Save))
                        SaveToStorage(row.Dir);

                    // Discard is a first-class action: it is what you reach for when you are done with a
                    // pack, and burying it in an overflow menu made "done" the hardest step to find.
                    if (Button("Discard",
                            "Delete this pack's ADK copy, its ModDev build, or both. Storage is never affected.",
                            false))
                        ShowDiscardMenu(row, GUILayoutUtility.GetLastRect());

                    if (GUILayout.Button("More", ActionButtonStyle, GUILayout.Width(56), GUILayout.Height(ActionButtonHeight)))
                        ShowMoreMenu(row);
                }
            }

            EditorGUILayout.Space(2);

            // one-line hint naming the recommended next step
            var hint = HintFor(row);
            if (hint != null)
                GUILayout.Label(hint, EditorStyles.wordWrappedMiniLabel);
        }

        private static string HintFor(PackRow row)
        {
            if (!row.InAdk)
                return row.InStorage ? "Load from Storage into the ADK to start editing." : null;
            if (row.Sync == SyncDirection.Diverged)
                return "ModDev and the ADK have diverged - each has files the other doesn't. Check " +
                       "differences below before Pull or Push Text: either one replaces every " +
                       "artifact in one direction, Data and Terrain included.";
            if (row.Sync == SyncDirection.ModDevAhead && row.ModDevOnlyFiles > 0)
                return "ModDev has files the ADK doesn't - either authored in-game (Pull them into " +
                       "the ADK) or deleted in the ADK (Build to drop them). Check the differences " +
                       "below before choosing.";
            if (row.Sync == SyncDirection.ModDevAhead)
                return "You edited files in ModDev - Pull them into the ADK, then Save to Storage.";
            if (!row.InModDev) return "Build to load this pack in the game.";
            if (row.Sync == SyncDirection.AdkAhead) return "ADK text changed - Push Text to update the game instantly (or Build to rebuild assets).";
            if (!row.InStorage) return "Save to Storage to keep this pack in your library.";
            return "In sync across ModDev, ADK, and Storage.";
        }

        private void ShowMoreMenu(PackRow row)
        {
            var menu = new GenericMenu();
            var dir = row.Dir;

            if (row.InAdk)
                menu.AddItem(new GUIContent("Show ADK folder in Project"), false, () => ShowAdkFolder(dir));
            if (row.InStorage)
            {
                // Named apart from the "Load from Storage" button: when a copy already exists this is a
                // different intent: throw away the ADK work and start from the library again.
                menu.AddItem(new GUIContent("Reload from Storage (replace ADK)"), false, () => LoadFromStorage(dir));
                // Without this there is no route from the window to the folder you'd need after a save
                // you regret.
                menu.AddItem(new GUIContent("Reveal Storage folder"), false, () => RevealStorage(dir));
            }

            if (row.InModDev)
            {
                menu.AddItem(new GUIContent("Reveal in ModDev"), false, () => Reveal(dir));

                // Granular pulls, for when ModDev diverged in only one file type.
                if (row.Sync is SyncDirection.ModDevAhead or SyncDirection.Diverged)
                {
                    menu.AddItem(new GUIContent("Pull ModDev Edits/Definitions.json"), false, () => PullModDevFile(dir, DataArtifact.Definitions));
                    menu.AddItem(new GUIContent("Pull ModDev Edits/Info.json"), false, () => PullModDevFile(dir, DataArtifact.Info));
                    // The payload most needs this: a map authored in-game exists only in ModDev, and
                    // the all-artifacts Pull would drag Definitions/Info backwards as the price of
                    // rescuing it.
                    menu.AddItem(new GUIContent("Pull ModDev Edits/Data files"), false, () => PullModDevFile(dir, DataArtifact.Payload));
                }

            }

            menu.ShowAsContext();
        }

        private void DrawMetadata(bool editable)
        {
            _foldMeta = EditorGUILayout.Foldout(_foldMeta, "Metadata", true);
            if (!_foldMeta) return;

            var info = _selStore.GetInfo();
            EditorGUI.indentLevel++;

            if (!editable)
                EditorGUILayout.LabelField("Read-only - Load from Storage into the ADK to edit.", EditorStyles.miniLabel);

            EditorGUI.BeginChangeCheck();
            using (new EditorGUI.DisabledScope(!editable))
            {
                info.Identifier = ValidatedField("Identifier", info.Identifier, _selEditor.ValidateIdentifier(info.Identifier));
                info.Name = EditorGUILayout.TextField("Name", info.Name ?? "");
                info.Author = EditorGUILayout.TextField("Author", info.Author ?? "");
                info.Version = ValidatedField("Version", info.Version, _selEditor.ValidateVersion(info.Version));

                // Pass the raw index (may be -1 for an unrepresentable stored scope) so an untouched
                // popup shows blank and preserves the value; only overwrite on a real user change.
                var scopeIndex = string.IsNullOrWhiteSpace(info.Scope) ? 0
                    : Array.IndexOf(ScopeOptions, info.Scope.Trim().ToLowerInvariant());
                var newScope = EditorGUILayout.Popup("Scope", scopeIndex, ScopeLabels);
                if (newScope != scopeIndex && newScope >= 0)
                    info.Scope = newScope == 0 ? null : ScopeOptions[newScope];

                // Teaching caption - explains what the setting does, rather than validating it.
                var describe = newScope >= 0 ? newScope : Mathf.Max(0, scopeIndex);
                if (describe < ScopeDescriptions.Length)
                    EditorGUILayout.LabelField(" ", ScopeDescriptions[describe], EditorStyles.wordWrappedMiniLabel);
                // A stored value the popup can't represent (e.g. reserved 'client') surfaces its error inline
                // rather than only in the Save-time dialog.
                var scopeValidation = _selEditor.ValidateScope(info.Scope);
                if (!scopeValidation.IsValid)
                    EditorGUILayout.HelpBox(scopeValidation.ErrorMessage, MessageType.Error);

                info.Url = ValidatedField("URL", info.Url ?? "", _selEditor.ValidateUrl(info.Url));
                info.License = EditorGUILayout.TextField("License", info.License ?? "");
                info.Description = EditorGUILayout.TextField("Description", info.Description ?? "");
            }
            if (EditorGUI.EndChangeCheck())
                _detailDirty = true;

            EditorGUI.indentLevel--;
        }

        private string ValidatedField(string label, string value, AssetPackInfoEditor.ValidationResult validation)
        {
            var result = EditorGUILayout.TextField(label, value);
            if (!validation.IsValid)
                EditorGUILayout.HelpBox(validation.ErrorMessage, MessageType.Error);
            return result;
        }

        // Row-level problems report where the row is, matching how the metadata fields report theirs.
        // Otherwise the only signal is a modal at commit time, listing errors far from what caused them.
        private static void RowError(string message) => EditorGUILayout.HelpBox(message, MessageType.Error);

        // A hint is not yet a problem: it explains why an action is unavailable for what you've typed.
        private void RowHint(string message) => GUILayout.Label(message, MiniColored(WarnColor));

        private void DrawDependencies(bool editable)
        {
            _foldDeps = EditorGUILayout.Foldout(_foldDeps, "Dependencies", true);
            if (!_foldDeps) return;

            EditorGUI.indentLevel++;
            DrawDepList("Required", _selStore.GetRequiredDependencies(),
                d => _selStore.RemoveRequiredDependency(d),
                d => _selStore.AddRequiredDependency(d), ref _newRequired, editable);
            DrawDepList("Optional", _selStore.GetOptionalDependencies(),
                d => _selStore.RemoveOptionalDependency(d),
                d => _selStore.AddOptionalDependency(d), ref _newOptional, editable);
            DrawDepList("Incompatible", _selStore.GetIncompatibleDependencies(),
                d => _selStore.RemoveIncompatibleDependency(d),
                d => _selStore.AddIncompatibleDependency(d), ref _newIncompatible, editable);
            EditorGUI.indentLevel--;
        }

        private void DrawDepList(string title, List<string> deps, Action<string> remove, Action<string> add, ref string scratch, bool editable)
        {
            EditorGUILayout.LabelField($"{title} ({deps.Count})", EditorStyles.miniBoldLabel);

            // Entries repeated within this list. Commit rejects them, so say which row is the problem
            // rather than leaving it to a modal listing the whole detail's errors.
            var duplicates = deps.GroupBy(d => d).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();

            string toRemove = null;
            for (var i = 0; i < deps.Count; i++)
            {
                // Snapshot validity from the value as it stands entering this event: the error below has
                // to be decided identically in this pass and in the Layout pass that preceded it, and
                // the field may rewrite deps[i] in between. The message therefore lags one event behind
                // the keystroke, which is what the metadata fields do too.
                var rowValidation = editable ? _selEditor.ValidateDependency(deps[i]) : default;
                var rowDuplicate = editable && duplicates.Contains(deps[i]);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (editable)
                    {
                        // Edited in place, by index, so correcting a typo doesn't reorder the list the way
                        // remove-then-add would.
                        EditorGUI.BeginChangeCheck();
                        var edited = EditorGUILayout.TextField(deps[i]);
                        if (EditorGUI.EndChangeCheck())
                        {
                            deps[i] = edited;
                            _detailDirty = true;
                        }
                        if (GUILayout.Button("Remove", GUILayout.Width(64)))
                            toRemove = deps[i];
                    }
                    else
                    {
                        EditorGUILayout.LabelField(deps[i]);
                        using (new EditorGUI.DisabledScope(true))
                            GUILayout.Button("Remove", GUILayout.Width(64));
                    }
                }

                // Errors only where they can be acted on; a read-only reference pack can't be fixed here.
                if (!editable) continue;
                if (!rowValidation.IsValid)
                    RowError(rowValidation.ErrorMessage);
                else if (rowDuplicate)
                    RowError($"Listed more than once in {title}.");
            }
            if (toRemove != null) { remove(toRemove); _detailDirty = true; }

            if (!editable) return;

            var scratchValidation = _selEditor.ValidateDependency(scratch);
            var alreadyListed = !string.IsNullOrWhiteSpace(scratch) && deps.Contains(scratch);
            using (new EditorGUILayout.HorizontalScope())
            {
                scratch = EditorGUILayout.TextField(scratch);
                using (new EditorGUI.DisabledScope(!scratchValidation.IsValid || alreadyListed))
                {
                    if (GUILayout.Button("Add", GUILayout.Width(64)))
                    {
                        try { add(scratch); scratch = ""; _detailDirty = true; }
                        catch (Exception e) { EditorUtility.DisplayDialog("Error", e.Message, "OK"); }
                    }
                }
            }

            // Say why Add is disabled. Silence on an empty field: nothing is wrong yet.
            if (alreadyListed)
                RowHint($"'{scratch}' is already in {title}.");
            else if (!string.IsNullOrEmpty(scratch) && !scratchValidation.IsValid)
                RowHint(scratchValidation.ErrorMessage);
        }

        private void DrawExports(bool editable)
        {
            _foldExports = EditorGUILayout.Foldout(_foldExports, "Module Exports", true);
            if (!_foldExports) return;

            EditorGUI.indentLevel++;

            // Keys claimed by more than one row. Commit rejects the collision; flag the rows that cause it.
            var duplicateKeys = _exportRows
                .Select(r => (r.Key ?? "").Trim())
                .Where(k => k.Length > 0)
                .GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key)
                .ToHashSet();

            int removeIndex = -1;
            for (int i = 0; i < _exportRows.Count; i++)
            {
                var er = _exportRows[i];

                // Same rule as the dependency rows: decide the errors from the pre-edit values so this
                // pass emits the controls its Layout pass recorded.
                var key = (er.Key ?? "").Trim();
                var path = (er.Path ?? "").Trim();
                // A wholly blank row is a row being started, not an error; commit drops it silently.
                var blankRow = key.Length == 0 && path.Length == 0;
                var keyValidation = _selEditor.ValidateExportKey(key);
                var pathValidation = _selEditor.ValidateScriptPath(path);
                var duplicateKey = key.Length > 0 && duplicateKeys.Contains(key);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (editable)
                    {
                        EditorGUI.BeginChangeCheck();
                        er.Key = EditorGUILayout.TextField(er.Key, GUILayout.Width(150));
                        EditorGUILayout.LabelField("->", GUILayout.Width(20));
                        er.Path = EditorGUILayout.TextField(er.Path);
                        if (EditorGUI.EndChangeCheck()) _detailDirty = true;
                        if (GUILayout.Button("Remove", GUILayout.Width(64)))
                            removeIndex = i;
                    }
                    else
                    {
                        EditorGUILayout.LabelField(er.Key, GUILayout.Width(150));
                        EditorGUILayout.LabelField("->", GUILayout.Width(20));
                        EditorGUILayout.LabelField(er.Path);
                    }
                }

                if (!editable || blankRow) continue;

                if (!keyValidation.IsValid)
                    RowError($"Key: {keyValidation.ErrorMessage}");
                else if (duplicateKey)
                    RowError($"Key: '{key}' is exported by more than one row.");

                if (!pathValidation.IsValid)
                    RowError($"Path: {pathValidation.ErrorMessage}");
            }
            if (removeIndex >= 0) { _exportRows.RemoveAt(removeIndex); _detailDirty = true; }

            if (editable)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Add Export", GUILayout.Width(90)))
                    {
                        _exportRows.Add(new ExportRow());
                        _detailDirty = true;
                    }
                    GUILayout.Label("key: './module' or 'domain/module'   path: 'Scripts/*.rl'", EditorStyles.miniLabel);
                }
            }
            EditorGUI.indentLevel--;
        }

        [NonSerialized] private GUIStyle _assetRowStyle;
        private GUIStyle AssetRowStyle => _assetRowStyle ??= new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            imagePosition = ImagePosition.ImageLeft,
            padding = new RectOffset(22, 4, 0, 0),
            fixedHeight = 20,
        };

        private void DrawAssets(PackRow row)
        {
            _foldAssets = EditorGUILayout.Foldout(_foldAssets, "Assets", true);
            if (!_foldAssets) return;

            // Assets are browsable only when they live in this Unity project (ADK or Shared). Storage-only
            // packs sit outside the project, so there is nothing to ping or open.
            if (!row.InAdk && !row.InShared)
            {
                GUILayout.Label("Load into the ADK to browse and select individual assets.", EditorStyles.miniLabel);
                return;
            }

            var assets = _selStore.EnumerateAssets().ToList();
            if (assets.Count == 0)
            {
                GUILayout.Label("No assets in this pack.", EditorStyles.miniLabel);
                return;
            }

            foreach (var group in assets.GroupBy(a => a.type).OrderBy(g => g.Key))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Space(6);
                    GUILayout.Label($"{group.Key} ({group.Count()})", EditorStyles.miniBoldLabel);
                }
                foreach (var asset in group)
                    DrawAssetRow(asset);
            }
        }

        // A single asset drawn like a Project-window entry: icon + name, whole row clickable to
        // select+ping (double-click opens), with a selection highlight and a consistent indent.
        private void DrawAssetRow(Common.Asset asset)
        {
            var path = _selStore.PathForAsset(asset);
            var exists = File.Exists(path);
            var icon = exists
                ? AssetDatabase.GetCachedIcon(path)
                : EditorGUIUtility.IconContent("console.warnicon.sml").image;
            var content = new GUIContent(asset.name, icon, exists ? path : path + "  (missing)");

            var rect = GUILayoutUtility.GetRect(content, AssetRowStyle, GUILayout.ExpandWidth(true));
            var isSelected = Selection.activeObject != null &&
                             AssetDatabase.GetAssetPath(Selection.activeObject) == path;

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                _selStore.SelectAsset(asset.name);
                if (e.clickCount == 2 && exists)
                    AssetDatabase.OpenAsset(AssetDatabase.LoadMainAssetAtPath(path));
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.Repaint)
            {
                if (isSelected)
                    EditorGUI.DrawRect(rect, new Color(0.30f, 0.50f, 0.85f, 0.25f));
                AssetRowStyle.Draw(rect, content, false, false, isSelected, false);
            }

            // The path is what you need in order to fix a broken reference, so it's spelled out rather
            // than left in the row's tooltip.
            if (!exists)
                EditorGUILayout.HelpBox($"File not found: {path}", MessageType.Warning);
        }

        private void DrawStatusBar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(_status, EditorStyles.miniLabel);
            }
        }

        // ------------------------------------------------------------------ detail commit / leave

        // Validates the whole detail (metadata + export scratch) and returns the list of problems.
        private List<string> CollectDetailErrors()
        {
            var errors = new List<string>();
            if (_selStore == null || _selEditor == null)
                return errors;

            var info = _selStore.GetInfo();
            void Check(string label, AssetPackInfoEditor.ValidationResult r) { if (!r.IsValid) errors.Add($"{label}: {r.ErrorMessage}"); }
            Check("Identifier", _selEditor.ValidateIdentifier(info.Identifier));
            Check("Version", _selEditor.ValidateVersion(info.Version));
            Check("Scope", _selEditor.ValidateScope(info.Scope));
            if (!string.IsNullOrEmpty(info.Url)) Check("URL", _selEditor.ValidateUrl(info.Url));

            // Dependencies are editable in place, which bypasses the store's own add-time validation,
            // so every entry is re-checked here, along with duplicates a rename could introduce.
            void CheckDeps(string label, List<string> deps)
            {
                var seen = new HashSet<string>();
                foreach (var dep in deps)
                {
                    Check($"{label} '{dep}'", _selEditor.ValidateDependency(dep));
                    if (!seen.Add(dep)) errors.Add($"{label} dependency '{dep}' is listed more than once.");
                }
            }
            CheckDeps("Required", _selStore.GetRequiredDependencies());
            CheckDeps("Optional", _selStore.GetOptionalDependencies());
            CheckDeps("Incompatible", _selStore.GetIncompatibleDependencies());

            var seenKeys = new HashSet<string>();
            foreach (var er in _exportRows)
            {
                var k = (er.Key ?? "").Trim();
                var p = (er.Path ?? "").Trim();
                if (k.Length == 0 && p.Length == 0) continue; // blank row, ignored on commit
                Check($"Export '{k}' key", _selEditor.ValidateExportKey(k));
                Check($"Export '{k}' path", _selEditor.ValidateScriptPath(p));
                if (!seenKeys.Add(k)) errors.Add($"Export key '{k}' is listed more than once.");
            }
            return errors;
        }

        // Writes the in-memory edit state to disk. Assumes it has already validated (CollectDetailErrors).
        private void ApplyDetail()
        {
            if (_selStore == null || _selEditor == null) return;

            // Rebuild exports from the scratch rows.
            foreach (var existing in _selEditor.GetExports().Keys.ToList())
                _selEditor.RemoveExport(existing);
            foreach (var er in _exportRows)
            {
                var k = (er.Key ?? "").Trim();
                var p = (er.Path ?? "").Trim();
                if (k.Length == 0 && p.Length == 0) continue;
                _selEditor.AddExport(k, p);
            }

            _selStore.Identifier = _selStore.GetInfo().Identifier;
            _selStore.Save();
            _detailDirty = false;
        }

        // Explicit Save from the detail toolbar, and the pre-flush every disk action runs first:
        // validate, report problems, then persist.
        private bool CommitDetail()
        {
            var errors = CollectDetailErrors();
            if (errors.Count > 0)
            {
                EditorUtility.DisplayDialog("Validation Errors", string.Join("\n", errors), "OK");
                return false;
            }
            if (!_detailDirty)
                return true;
            ApplyDetail();
            RebuildExportScratch(); // normalize (drop blank rows) so the view matches what was written
            SetStatus($"Applied edits to {_selStore.GetInfo().Identifier}");
            _rows = BuildRows();
            return true;
        }

        // Discards in-memory edits and reloads the pack from disk.
        private void ReloadSelection()
        {
            _selStore = null;
            _selEditor = null;
            _detailDirty = false;
            EnsureSelectionLoaded();
        }

        // Guards navigation away from a dirty detail. Valid edits are auto-committed (zero friction);
        // only genuinely invalid edits prompt, so the user isn't trapped or silently losing work.
        private bool TryLeaveDetail()
        {
            if (_selectedDir == null || !_detailDirty)
                return true;

            var errors = CollectDetailErrors();
            if (errors.Count == 0)
            {
                // CommitDetail (not bare ApplyDetail) so the list view + export scratch are refreshed too.
                CommitDetail();
                return true;
            }

            var discard = EditorUtility.DisplayDialog("Unsaved invalid edits",
                "This pack has Info.json edits that don't validate:\n\n" + string.Join("\n", errors) +
                "\n\nDiscard these edits, or keep editing?",
                "Discard & Leave", "Keep Editing");
            if (!discard)
                return false;

            ReloadSelection();
            return true;
        }

        // ------------------------------------------------------------------ actions

        private void CreatePack(string dirName, string identifier)
        {
            // The folder name comes from the pack name alone, so two different domains can collide on
            // one folder. Creating into it would overwrite the existing pack's Info.json with a blank
            // template, taking its identity, dependencies and exports with it.
            if (Directory.Exists(Path.Combine(AdkPath, dirName)))
            {
                EditorUtility.DisplayDialog("Create Asset Pack",
                    $"Assets/AssetPacks/{dirName} already exists.\n\nThe folder is named after the pack " +
                    "name alone (the domain is not part of it), so a pack of this name under another " +
                    "domain would land in the same folder. Pick a different pack name.",
                    "OK");
                return;
            }

            var store = AssetPackEditorStore.Empty(AdkPath, dirName, identifier);
            store.Save();
            _newDomain = "";
            _newPackName = "";
            _showCreate = false;
            _selectedDir = dirName;
            Refresh(true);
            SetStatus($"Created {identifier}");
        }

        private void LoadFromStorage(string dir)
        {
            var storage = Path.Combine(StoragePath, dir);
            var local = Path.Combine(AdkPath, dir);
            if (!Directory.Exists(storage))
            {
                EditorUtility.DisplayDialog("Load from Storage", $"{dir} is not in Storage.", "OK");
                return;
            }

            if (Directory.Exists(local))
            {
                var ok = EditorUtility.DisplayDialog("Load from Storage",
                    $"Replace the ADK copy of {dir} with the Storage version?\nLocal changes will be discarded.",
                    "Replace", "Cancel");
                if (!ok) return;
                Directory.Delete(local, true);
            }

            try
            {
                EditorUtility.DisplayProgressBar("Load from Storage", $"Copying {dir} into the ADK", 0.4f);
                Sync(storage, local);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            AssetDatabase.Refresh();
            Refresh(true);
            SetStatus($"Loaded {dir} from Storage");
        }

        /// <summary>
        /// True when the Storage copy holds a file written more recently than anything in the ADK copy.
        /// Compares newest-write across each tree: Load from Storage copies timestamps along with the
        /// files, so a freshly loaded pack reads as equal, and editing in the ADK puts the ADK ahead.
        /// </summary>
        private static bool StorageIsNewer(string storage, string local)
        {
            if (!Directory.Exists(storage) || !Directory.Exists(local))
                return false;

            var storageTime = NewestWrite(storage);
            var localTime = NewestWrite(local);
            // A second of slack: copies and filesystems don't preserve timestamps to the tick.
            return (storageTime - localTime).TotalSeconds > 1;
        }

        // Walks the tree the way Sync does: dot-prefixed *directories* are skipped whole (a .git or .idea
        // folder in Storage would otherwise date the pack and raise a warning about files Sync never
        // touches), as are dot-files and the .meta of a JSON file.
        private static DateTime NewestWrite(string directory)
        {
            var newest = DateTime.MinValue;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(".") || name.EndsWith(".json.meta", StringComparison.OrdinalIgnoreCase))
                    continue;
                var t = File.GetLastWriteTimeUtc(file);
                if (t > newest) newest = t;
            }

            foreach (var sub in Directory.EnumerateDirectories(directory))
            {
                if (Path.GetFileName(sub).StartsWith(".")) continue;
                var t = NewestWrite(sub);
                if (t > newest) newest = t;
            }

            return newest;
        }

        private void SaveToStorage(string dir)
        {
            if (string.IsNullOrEmpty(StoragePath) || !Directory.Exists(StoragePath))
            {
                EditorUtility.DisplayDialog("Save to Storage", "Set a Storage path first (Paths button).", "OK");
                return;
            }
            // Flush any open edits to this pack first; abort if they don't validate.
            if (_selStore != null && _selStore.DirectoryName == dir && !CommitDetail())
                return;

            var local = Path.Combine(AdkPath, dir);
            var storage = Path.Combine(StoragePath, dir);

            // Saving mirrors the ADK copy over Storage, which deletes anything Storage has that the ADK
            // lacks, and Storage is the durable copy. Confirming every save would nag the common case
            // (Storage is usually older, that's the point), so only the case that means real loss asks:
            // Storage is newer, i.e. it was edited somewhere else since this copy was loaded.
            if (StorageIsNewer(storage, local))
            {
                var ok = EditorUtility.DisplayDialog("Save to Storage",
                    $"The Storage copy of {dir} is newer than the ADK copy. It was probably edited " +
                    "elsewhere since you loaded it.\n\nSaving replaces it, and removes anything Storage " +
                    "has that the ADK copy doesn't.",
                    "Replace Storage copy", "Cancel");
                if (!ok) return;
            }
            try
            {
                EditorUtility.DisplayProgressBar("Save to Storage", $"Copying {dir} to Storage", 0.4f);
                Sync(local, storage);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Refresh(true);
            SetStatus($"Saved {dir} to Storage");
        }

        // Fast, forced push ADK -> ModDev: Definitions.json, scripts, Info.json and the payload.
        // Unlike Build it rebuilds no bundles and deletes nothing, so a definitions/script tweak
        // reaches the running game instantly (reload in-game to pick it up).
        private void PushTextToModDev(string dir)
        {
            var game = Path.Combine(ModDevPath, dir);
            if (!Directory.Exists(game))
            {
                EditorUtility.DisplayDialog("Push Text",
                    $"{dir} hasn't been built into ModDev yet. Build it once first, then Push Text for fast edits.", "OK");
                return;
            }
            if (!ConfirmReplacingNewer(dir, towardAdk: false, "Push Text"))
                return;

            // Flush open edits so the pushed Info.json/Definitions reflect them.
            if (_selStore != null && _selStore.DirectoryName == dir && !CommitDetail())
                return;

            AssetPackSyncHelper.CopyDataArtifacts(
                Path.Combine(AdkPath, dir), game);

            Refresh(true);
            SetStatus($"Pushed text to ModDev: ADK -> ModDev ({dir})");
        }

        /// <summary>
        /// Guards Pull and Push Text, which each copy every artifact one way regardless of
        /// which side is newer. Names the specific files that would go backwards; silent when none
        /// would, so the common case stays a single click. Returns false to abort the action.
        /// </summary>
        private static bool ConfirmReplacingNewer(string dir, bool towardAdk, string action, DataArtifact? only = null)
        {
            var replaced = AssetPackSyncHelper.FilesReplacedWithOlder(
                Path.Combine(towardAdk ? ModDevPath : AdkPath, dir),
                Path.Combine(towardAdk ? AdkPath : ModDevPath, dir),
                only);
            if (replaced.Count == 0)
                return true;

            // The at-risk files sit on the destination side; that is what makes them at risk.
            var newerSide = towardAdk ? "the ADK" : "ModDev";
            var olderSide = towardAdk ? "ModDev" : "the ADK";
            return EditorUtility.DisplayDialog($"{action} replaces newer files",
                $"These files are newer in {newerSide} than in {olderSide}, and {action} would replace " +
                $"them with the older {olderSide} copy:\n\n  {Listed(replaced)}" +
                "\n\nThose edits cannot be recovered.",
                $"Replace and {action}", "Cancel");
        }

        // Guards a Pull, which overwrites the ADK copy of `dir` from ModDev. If that pack is open with
        // unsaved edits, warn before discarding them; returns false to abort the action.
        private bool ConfirmDiscardOpenEdits(string dir, string action)
        {
            if (_selStore == null || _selStore.DirectoryName != dir || !_detailDirty)
                return true;
            var ok = EditorUtility.DisplayDialog("Unsaved edits",
                $"You have unsaved edits to {dir}. {action} overwrites the ADK copy and will discard them.\n\nContinue?",
                "Discard & Continue", "Cancel");
            if (ok)
                _detailDirty = false; // accepted; the following Refresh(true) reloads from disk
            return ok;
        }

        private void PullModDevEdits(string dir)
        {
            // Disk first: ConfirmDiscardOpenEdits clears _detailDirty when accepted, so aborting after
            // it would drop the in-memory edits without performing the Pull they were cleared for.
            if (!ConfirmReplacingNewer(dir, towardAdk: true, "Pull")) return;
            if (!ConfirmDiscardOpenEdits(dir, "Pull")) return;

            AssetPackSyncHelper.CopyDataArtifacts(
                Path.Combine(ModDevPath, dir), Path.Combine(AdkPath, dir));

            AssetDatabase.Refresh();
            Refresh(true);
            SetStatus($"Pulled ModDev edits: ModDev -> ADK ({dir})");
        }

        private void PullModDevFile(string dir, DataArtifact kind)
        {
            if (!ConfirmReplacingNewer(dir, towardAdk: true, $"Pulling {kind}", kind)) return;
            if (!ConfirmDiscardOpenEdits(dir, $"Pulling {kind}")) return;

            AssetPackSyncHelper.CopyDataArtifacts(
                Path.Combine(ModDevPath, dir), Path.Combine(AdkPath, dir),
                kind);

            AssetDatabase.Refresh();
            Refresh(true);
            SetStatus($"Pulled {kind} from ModDev: ModDev -> ADK ({dir})");
        }

        private void Build()
        {
            if (_selStore != null)
                BuildPack(_selStore.DirectoryName);
        }

        private const string NoModDevPath = "Set a ModDev (build) path first (Paths button).";

        // Builds a single ADK pack and its dependencies into ModDev.
        private void BuildPack(string dir)
        {
            var buildPath = ModDevPath;
            if (string.IsNullOrEmpty(buildPath))
            {
                EditorUtility.DisplayDialog("Build", NoModDevPath, "OK");
                return;
            }

            var target = LoadAdkStores().FirstOrDefault(s => s.DirectoryName == dir);
            if (target == null)
            {
                SetStatus($"Build failed: {dir} not found in the ADK.");
                return;
            }

            if (!ConfirmBuildLosses(buildPath, SortedWithDependencies(dir), "Build"))
                return;

            // Flush unsaved edits if this pack is the one open in the detail view; abort if invalid.
            if (_selStore != null && _selStore.DirectoryName == dir && !CommitDetail())
                return;

            // Re-resolve after the flush: it may have rewritten the identifier or the dependencies
            // the sort is built from.
            var sorted = SortedWithDependencies(dir);
            if (!sorted.Any(s => s.DirectoryName == dir))
            {
                SetStatus($"Build failed: {dir} has dependency errors. See the Console for details.");
                return;
            }
            AssetPackEditorStore.BuildBundles(buildPath, sorted);

            Refresh(true);
            SetStatus($"Built {target.Identifier} -> ModDev");
        }

        private void BuildAllAdk()
        {
            var buildPath = ModDevPath;
            if (string.IsNullOrEmpty(buildPath))
            {
                EditorUtility.DisplayDialog("Build", NoModDevPath, "OK");
                return;
            }

            if (!ConfirmBuildLosses(buildPath, SortedAllAdk().Sorted, "Build ADK Packs"))
                return;

            // Flush any unsaved edits to the currently open ADK pack so the build picks them up.
            if (_selStore != null && _rows.Any(r => r.Dir == _selStore.DirectoryName && r.InAdk) && !CommitDetail())
                return;

            var (sorted, total) = SortedAllAdk();
            // The sort also returns the reference packs the build needs, which BuildBundles skips.
            var built = sorted.Count(s => !s.IsReferencePack);
            if (built == 0)
            {
                SetStatus(total == 0
                    ? "No ADK packs to build."
                    : "Build failed: every ADK pack has dependency errors. See the Console for details.");
                return;
            }
            AssetPackEditorStore.BuildBundles(buildPath, sorted);

            Refresh(true);
            SetStatus(built == total
                ? $"Built {built} ADK pack(s) -> ModDev"
                : $"Built {built} of {total} ADK packs -> ModDev. The rest have dependency errors; see the Console.");
        }

        /// <summary>
        /// Formats a file list for a dialog, capped. EditorUtility.DisplayDialog does not scroll, and
        /// a map pack's Terrain runs to hundreds of tiles -- an uncapped list makes the dialog
        /// unreadable, and its buttons unreachable, exactly when it has the most to say.
        /// </summary>
        private static string Listed(IReadOnlyList<string> files)
        {
            const int Shown = 15;
            var listed = string.Join("\n  ", files.Take(Shown));
            if (files.Count > Shown)
                listed += $"\n  ...and {files.Count - Shown} more";
            return listed;
        }

        /// <summary>Every ADK store, plus the shared/reference packs dependency resolution needs.</summary>
        private static List<AssetPackEditorStore> LoadAdkStores()
        {
            var stores = AssetPackEditorStore.FindAllStores(AdkPath)
                .Select(d => AssetPackEditorStore.Load(AdkPath, d))
                .ToList();
            stores.AddRange(AssetPackEditorStore.LoadSharedPacks());
            return stores;
        }

        /// <summary>
        /// One pack and the dependencies a build would pull in with it. Read from disk each time so
        /// the pre-flush confirm and the post-flush build both see the state they are asking about.
        /// </summary>
        private static List<AssetPackEditorStore> SortedWithDependencies(string dir)
        {
            var allStores = LoadAdkStores();
            var target = allStores.FirstOrDefault(s => s.DirectoryName == dir);
            return target == null
                ? new List<AssetPackEditorStore>()
                : SortStoresByDependencies(new List<AssetPackEditorStore> { target }, allStores);
        }

        /// <summary>
        /// Every ADK pack as a build target, in dependency order, and how many distinct packs were targeted.
        /// </summary>
        private static (List<AssetPackEditorStore> Sorted, int TargetCount) SortedAllAdk()
        {
            var allStores = LoadAdkStores();
            // Reference packs resolve dependencies but are never built themselves.
            var adkStores = allStores.Where(s => !s.IsReferencePack).ToList();
            // The sort keys packs by identifier, so folders sharing one count once.
            var targetCount = adkStores.Select(s => s.Identifier).Distinct().Count();
            var sorted = adkStores.Count == 0
                ? new List<AssetPackEditorStore>()
                : SortStoresByDependencies(adkStores, allStores);
            return (sorted, targetCount);
        }

        /// <summary>
        /// Guards a build, which reproduces the ADK copy into ModDev without asking: it mirrors the
        /// payload (so it deletes payload files the ADK lacks, anywhere in the pack) and overwrites
        /// every artifact regardless of which side is newer. ModDev is not just output -- the running game
        /// writes maps, terrain and definitions there -- so this names what would be lost and lets
        /// the build be cancelled. Silent when nothing would be, so the common build stays one click.
        ///
        /// Scans the dependency-resolved set rather than the selected row, because that is what the
        /// build actually writes, and skips reference packs for the same reason: BuildBundles
        /// produces no output for those.
        /// </summary>
        private static bool ConfirmBuildLosses(
            string buildPath, List<AssetPackEditorStore> stores, string action)
        {
            var lost = new List<string>();
            foreach (var store in stores)
            {
                if (store.IsReferencePack)
                    continue;

                foreach (var relativePath in AssetPackSyncHelper.FilesLostToBuild(
                             store.SourcePath, Path.Combine(buildPath, store.DirectoryName)))
                    lost.Add($"{store.DirectoryName}/{relativePath}");
            }

            if (lost.Count == 0)
                return true;

            return EditorUtility.DisplayDialog($"{action} overwrites ModDev files",
                $"{action} would delete or replace these ModDev files with the ADK copy:\n\n  {Listed(lost)}\n\n" +
                "Anything you authored in-game -- a map, its terrain, a definition edited in the " +
                "Definition Editor -- lives here and may exist nowhere else. Pull the pack first to keep it.",
                $"Overwrite and {action}", "Cancel");
        }

        private void Reveal(string dir)
        {
            var path = Path.Combine(ModDevPath, dir);
            if (Directory.Exists(path))
                EditorUtility.RevealInFinder(path);
        }

        private void RevealStorage(string dir)
        {
            if (string.IsNullOrEmpty(StoragePath)) return;
            var path = Path.Combine(StoragePath, dir);
            if (Directory.Exists(path))
                EditorUtility.RevealInFinder(path);
        }

        // Selects and pings the pack's source folder in Unity's Project window.
        private void ShowAdkFolder(string dir)
        {
            var folder = Path.Combine(AdkPath, dir).Replace('\\', '/');
            var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(folder);
            if (obj != null)
            {
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
            }
            else
            {
                SetStatus($"ADK folder not found in Project: {folder}");
            }
        }

        // Deletes just this pack's built output from ModDev (leaving the ADK source untouched).
        private void CleanModDevBuild(PackRow row)
        {
            var dir = row.Dir;
            var game = Path.Combine(ModDevPath, dir);
            if (!Directory.Exists(game))
            {
                SetStatus($"No ModDev build to clean for {dir}.");
                return;
            }
            var ok = EditorUtility.DisplayDialog("Discard ModDev build",
                $"Delete the built copy of {dir} from ModDev?\nThe game will stop loading it. Your ADK " +
                "working copy and Storage are not affected." + UnpulledWarning(row),
                "Delete", "Cancel");
            if (!ok) return;

            Directory.Delete(game, true);
            // Only ModDev output changed; keep the open pack's in-memory edits (Refresh, not Refresh(true)).
            Refresh();
            SetStatus($"Deleted the ModDev build of {dir}");
        }

        /// <summary>
        /// Discard covers three different intents (clear the working copy, clear what the game loads,
        /// or both), so the button offers them rather than picking one. Each is destructive and confirms
        /// separately. Storage is never touched by any of them.
        /// </summary>
        private void ShowDiscardMenu(PackRow row, Rect buttonRect)
        {
            var dir = row.Dir;

            // Nothing in ModDev means there is only one thing Discard could mean; don't make the user
            // pick from a menu of one.
            if (!row.InModDev)
            {
                // Runs inline, unlike the menu items below, which fire after OnGUI returns. Discarding
                // clears the selection, so the rest of the detail view has nothing left to draw from.
                // Bail out of this pass the same way "< Packs" does.
                if (DiscardFromAdk(row, alsoBuild: false))
                    GUIUtility.ExitGUI();
                return;
            }

            var menu = new GenericMenu();
            if (row.InAdk)
            {
                menu.AddItem(new GUIContent("Discard ADK copy"), false, () => DiscardFromAdk(row, false));
                menu.AddItem(new GUIContent("Discard ADK copy and ModDev build"), false, () => DiscardFromAdk(row, true));
                menu.AddSeparator("");
            }
            // Deleting only the build is how you make the game stop loading a pack while keeping the work.
            menu.AddItem(new GUIContent("Delete ModDev build only"), false, () => CleanModDevBuild(row));
            menu.DropDown(buttonRect);
        }

        // ModDev holding unpulled edits is the one case where deleting a "build" destroys original work,
        // so the dialog says so instead of leaving "built copy" to imply regenerable output.
        private static string UnpulledWarning(PackRow row) =>
            row.Sync is SyncDirection.ModDevAhead or SyncDirection.Diverged
                ? "\n\nModDev has edits you have not pulled into the ADK. Deleting it destroys them."
                : string.Empty;

        /// <summary>Returns true when the pack was deleted, false when the user cancelled.</summary>
        private bool DiscardFromAdk(PackRow row, bool alsoBuild)
        {
            var dir = row.Dir;
            var scope = alsoBuild
                ? $"Delete the ADK working copy of {dir} and its ModDev build?"
                : $"Delete the ADK working copy of {dir}?";
            // Claiming Storage is unaffected reads as reassurance, which is misleading when there is no
            // Storage copy to be unaffected; then the ADK copy is the last one.
            var fallback = row.InStorage
                ? "Your Storage copy is not affected."
                : $"{dir} has never been saved to Storage, so this is the only copy.";
            var ok = EditorUtility.DisplayDialog("Discard",
                $"{scope}\nThis cannot be undone. {fallback}" + (alsoBuild ? UnpulledWarning(row) : string.Empty),
                "Delete", "Cancel");
            if (!ok) return false;

            var local = Path.Combine(AdkPath, dir);
            if (Directory.Exists(local)) Directory.Delete(local, true);
            var meta = local + ".meta";
            if (File.Exists(meta)) File.Delete(meta);

            if (alsoBuild)
            {
                var game = Path.Combine(ModDevPath, dir);
                if (Directory.Exists(game)) Directory.Delete(game, true);
            }

            AssetDatabase.Refresh();
            _selectedDir = null;
            _detailDirty = false;
            Refresh();
            SetStatus(alsoBuild ? $"Discarded {dir} from the ADK and ModDev" : $"Discarded {dir} from the ADK");
            return true;
        }

        private void SetStatus(string message)
        {
            _status = message;
            Debug.Log($"[Asset Packs] {message}");
        }

        // ------------------------------------------------------------------ helpers

        private static void Sync(string source, string destination)
        {
            DirectoryUtilities.OverwriteDirectoryRecursively(source, destination, DirectoryUtilities.IgnoreDotFilesAndJsonMeta);
        }

        private readonly Dictionary<Color, GUIStyle> _coloredStyles = new Dictionary<Color, GUIStyle>();
        private readonly Dictionary<Color, GUIStyle> _wrappedStyles = new Dictionary<Color, GUIStyle>();

        private GUIStyle MiniColored(Color color)
        {
            if (!_coloredStyles.TryGetValue(color, out var style))
            {
                style = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
                style.normal.textColor = color;
                _coloredStyles[color] = style;
            }
            return style;
        }

        /// <summary>Wrapping variant, cached separately: a sync message runs to a sentence.</summary>
        private GUIStyle MiniWrapped(Color color)
        {
            if (!_wrappedStyles.TryGetValue(color, out var style))
            {
                style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                style.normal.textColor = color;
                _wrappedStyles[color] = style;
            }
            return style;
        }

        private const float ActionButtonHeight = 26f;

        // Action buttons carry the window's real verbs, so they get height, padding and breathing room
        // rather than defaulting to the same weight as an inline field control.
        [NonSerialized] private GUIStyle _actionButtonStyle;
        private GUIStyle ActionButtonStyle => _actionButtonStyle ??= new GUIStyle(GUI.skin.button)
        {
            padding = new RectOffset(12, 12, 4, 4),
            margin = new RectOffset(2, 2, 2, 2),
            fontStyle = FontStyle.Bold,
        };

        private bool Button(string label, string tooltip, bool primary)
        {
            var prev = GUI.backgroundColor;
            if (primary) GUI.backgroundColor = Accent;
            var clicked = GUILayout.Button(new GUIContent(label, tooltip), ActionButtonStyle,
                GUILayout.Height(ActionButtonHeight), GUILayout.MinWidth(64));
            GUI.backgroundColor = prev;
            return clicked;
        }

        // Canonical scope strings written to Info.json; index 0 (unscoped) is the default and omitted.
        private static readonly string[] ScopeOptions = { "unscoped", "optional" };
        private static readonly string[] ScopeLabels = { "Unscoped (default)", "Optional" };
        private static readonly string[] ScopeDescriptions =
        {
            "Everyone needs it: recorded in saves and required to match across all multiplayer peers.",
            "Runs where installed; peers who lack it can still join. For supplementary or host-side content.",
        };

        private static string ValidateIdentifierPart(string value, string fieldName)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (value.Length < 2) return $"{fieldName} must be at least 2 characters.";
            if (value != value.ToLowerInvariant()) return $"{fieldName} must be lowercase.";
            if (value.Contains("/")) return $"{fieldName} must not contain '/'.";
            if (value.Contains(" ")) return $"{fieldName} must not contain spaces. Use hyphens.";
            if (value.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '.')))
                return $"{fieldName} allows only lowercase letters, digits, hyphens, and periods.";
            return null;
        }

        // ------------------------------------------------------------------ build dependency ordering
        // Self-contained copy so this window does not depend on the legacy editor windows.

        private static List<AssetPackEditorStore> SortStoresByDependencies(
            List<AssetPackEditorStore> buildStores, List<AssetPackEditorStore> availableStores)
        {
            var allPacks = new Dictionary<string, AssetPackInfo>();
            var storesByIdentifier = new Dictionary<string, AssetPackEditorStore>();

            foreach (var store in availableStores)
            {
                var info = store.GetInfo() ?? new AssetPackInfo
                {
                    Identifier = store.Identifier,
                    Name = store.Name,
                    Version = "1.0.0"
                };
                allPacks[info.Identifier] = info;
                storesByIdentifier[info.Identifier] = store;
            }

            var needed = GamePackDependencyHelper.CollectRequired(buildStores.Select(s => s.Identifier), allPacks);

            var neededPacks = allPacks.Where(kvp => needed.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            var validation = GamePackDependencyHelper.ValidateAndSort(neededPacks);
            foreach (var error in validation.Errors)
                Debug.LogError($"Asset pack dependency error: {error}");

            var sorted = new List<AssetPackEditorStore>();
            foreach (var info in validation.ValidPacks.OfType<AssetPackInfo>())
                if (storesByIdentifier.TryGetValue(info.Identifier, out var store))
                    sorted.Add(store);
            return sorted;
        }
    }
}
