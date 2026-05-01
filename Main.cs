using Rage;
using Rage.Attributes;
using Rage.Native;
using RAGENativeUI;
using RAGENativeUI.Elements;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Globalization;
using System.Windows.Forms;
using System.Linq;

[assembly: Plugin("LSR Track Creator (Dev)", Author = "Peter Badoingy", Description = "Track Creator for Los Santos RED")]

namespace RaceTrackCreator
{
    public static class EntryPoint
    {
        private static List<Vector3> checkpoints = new List<Vector3>();
        private static List<StartPos> starts = new List<StartPos>();
        private static List<Vehicle> previewVehicles = new List<Vehicle>();
        private static List<Blip> previewBlips = new List<Blip>();

        private enum StartType { Lined, Drag, Staggered }
        private enum StartSide { Left, Right }
        private enum ExportOrder { PlayerFirst, PlayerLast }

        private static StartType currentStartType = StartType.Lined;
        private static StartSide currentStartSide = StartSide.Right;
        private static ExportOrder currentExportOrder = ExportOrder.PlayerFirst;
        private static float sideMultiplier = 1.0f;

        private static bool startsLocked = false;
        private static bool snapToRoad = false;

        private static int carsWide = 1;
        private static int rowsBack = 0;
        private static float sideSpacing = 3.5f;

        private static string trackName = "My_Custom_Race";
        private const int MaxStarts = 8;

        private static MenuPool pool = new MenuPool();
        private static UIMenu mainMenu;
        private static UIMenuCheckboxItem lockItem;

        private static Keys MenuKey = Keys.F8;
        private static Keys ExportKey = Keys.F6;
        private static Keys AddCheckpointKey = Keys.F5;
        private static Keys ClearAllKey = Keys.F7;

        public static void Main()
        {
            LoadSettings();
            Game.DisplayNotification($"~b~Track Creator (Dev)~w~ loaded.\n~y~{MenuKey}~w~ to Open Menu");

            mainMenu = new UIMenu("Track Creator", "Track Setup");
            mainMenu.SetBannerType(Color.Red);
            pool.Add(mainMenu);

            UIMenuItem nameItem = new UIMenuItem("Set Track Name", "Current: " + trackName);
            nameItem.Activated += (m, i) => SetTrackName(nameItem);
            mainMenu.AddItem(nameItem);

            UIMenuListItem typeItem = new UIMenuListItem("Start Type", new List<dynamic> { "Lined", "Drag", "Staggered" }, 0);
            typeItem.OnListChanged += (s, i) => { currentStartType = (StartType)i; UpdateStarts(); };
            mainMenu.AddItem(typeItem);

            UIMenuListItem sideItem = new UIMenuListItem("Start Side", new List<dynamic> { "Left", "Right" }, 1);
            sideItem.OnListChanged += (s, i) =>
            {
                currentStartSide = (StartSide)i;
                sideMultiplier = (currentStartSide == StartSide.Right) ? 1.0f : -1.0f;
                UpdateStarts();
            };
            mainMenu.AddItem(sideItem);

            UIMenuListItem wideItem = new UIMenuListItem("Cars Wide", new List<dynamic> { 1, 2, 3, 4, 5 }, 0);
            wideItem.OnListChanged += (s, i) => { carsWide = wideItem.Index + 1; UpdateStarts(); };
            mainMenu.AddItem(wideItem);

            UIMenuListItem rowsItem = new UIMenuListItem("Rows Back", new List<dynamic> { 0, 1, 2, 3, 4 }, 0);
            rowsItem.OnListChanged += (s, i) => { rowsBack = rowsItem.Index; UpdateStarts(); };
            mainMenu.AddItem(rowsItem);

            List<dynamic> spacingValues = new List<dynamic> { 1.5f, 2.0f, 2.5f, 3.0f, 3.5f, 4.0f, 4.5f, 5.0f, 5.5f, 6.0f, 6.5f, 7.0f, 7.5f, 8.0f, 8.5f, 9.0f };
            UIMenuListItem spaceItem = new UIMenuListItem("Side Spacing", spacingValues, 3);
            spaceItem.OnListChanged += (s, i) => { sideSpacing = (float)spacingValues[i]; UpdateStarts(); };
            mainMenu.AddItem(spaceItem);

            UIMenuListItem orderItem = new UIMenuListItem("Export Order", new List<dynamic> { "Player First", "Player Last" }, 0);
            orderItem.OnListChanged += (s, i) => { currentExportOrder = (ExportOrder)i; };
            mainMenu.AddItem(orderItem);

            lockItem = new UIMenuCheckboxItem("Lock Starts & Hide", startsLocked, "Freezes grid and hides preview vehicles.");
            lockItem.CheckboxEvent += (i, isChecked) =>
            {
                startsLocked = isChecked;
                if (startsLocked) { ClearPreviewVehicles(); Game.DisplayNotification("~g~Starts Locked!"); }
                else { UpdateStarts(); Game.DisplayNotification("~y~Starts Unlocked!"); }
            };
            mainMenu.AddItem(lockItem);

            UIMenuCheckboxItem snapItem = new UIMenuCheckboxItem("Snap Checkpoint to Road", snapToRoad, "Uses nearest road node for placement.");
            snapItem.CheckboxEvent += (i, isChecked) => { snapToRoad = isChecked; };
            mainMenu.AddItem(snapItem);

            UIMenuItem addItem = new UIMenuItem($"Add Checkpoint ({AddCheckpointKey})");
            addItem.Activated += (m, i) => AddCheckpoint();
            mainMenu.AddItem(addItem);

            UIMenuItem undoItem = new UIMenuItem("Undo Last Checkpoint");
            undoItem.Activated += (m, i) => RemoveLastCheckpoint();
            mainMenu.AddItem(undoItem);

            UIMenuItem expItem = new UIMenuItem($"Export C# Code ({ExportKey})");
            expItem.Activated += (m, i) => ExportCSharp();
            mainMenu.AddItem(expItem);

            UIMenuItem clrItem = new UIMenuItem($"Clear All ({ClearAllKey})");
            clrItem.Activated += (m, i) => ClearAll();
            mainMenu.AddItem(clrItem);

            while (true)
            {
                GameFiber.Yield();
                pool.ProcessMenus();

                if (Game.IsKeyDown(MenuKey))
                {
                    mainMenu.Visible = !mainMenu.Visible;
                    if (!mainMenu.Visible) ClearPreviewVehicles();
                    else if (!startsLocked) UpdateStarts();
                }

                foreach (Vector3 cp in checkpoints)
                {
                    NativeFunction.Natives.DRAW_MARKER(6, cp.X, cp.Y, cp.Z, 0f, 0f, 0f, -90f, 0f, 0f, 4f, 4f, 4f, 0, 150, 255, 180, false, false, 2, false, 0, 0, false);
                }

                if (checkpoints.Count > 0)
                {
                    float dist = Vector3.Distance(Game.LocalPlayer.Character.Position, checkpoints.Last());
                    DrawSimpleText($"Distance to Last Checkpoint: {dist:F0}m", 0.5f, 0.88f);
                }

                if (mainMenu.Visible)
                {
                    if (Game.IsKeyDown(AddCheckpointKey)) AddCheckpoint();
                    if (Game.IsKeyDown(ExportKey)) ExportCSharp();
                    if (Game.IsKeyDown(ClearAllKey)) ClearAll();
                }
                else if (Game.IsKeyDown(AddCheckpointKey)) AddCheckpoint();
            }
        }

        /// <summary>
        /// Loads plugin settings from TrackCreator.ini.
        /// </summary>
        private static void LoadSettings()
        {
            string path = Path.Combine("Plugins", "TrackCreator.ini");
            InitializationFile ini = new InitializationFile(path);
            MenuKey = ini.ReadEnum<Keys>("Keys", "MenuKey", Keys.F8);
            ExportKey = ini.ReadEnum<Keys>("Keys", "ExportKey", Keys.F6);
            AddCheckpointKey = ini.ReadEnum<Keys>("Keys", "AddCheckpointKey", Keys.F5);
            ClearAllKey = ini.ReadEnum<Keys>("Keys", "ClearAllKey", Keys.F7);
        }

        /// <summary>
        /// Handles onscreen keyboard input for naming the track.
        /// </summary>
        private static void SetTrackName(UIMenuItem item)
        {
            NativeFunction.Natives.DISPLAY_ONSCREEN_KEYBOARD(6, "FMMC_KEY_TIP8", "", trackName, "", "", "", 30);
            while (NativeFunction.Natives.UPDATE_ONSCREEN_KEYBOARD<int>() == 0) GameFiber.Yield();
            string result = NativeFunction.Natives.GET_ONSCREEN_KEYBOARD_RESULT<string>();
            if (!string.IsNullOrWhiteSpace(result))
            {
                trackName = result.Replace(" ", "_");
                item.Description = "Current: " + trackName;
            }
        }

        /// <summary>
        /// Updates the starting grid positions based on player vehicle and formation type.
        /// </summary>
        private static void UpdateStarts()
        {
            if (startsLocked) return;
            starts.Clear();
            ClearPreviewVehicles();
            Vehicle pv = Game.LocalPlayer.Character.CurrentVehicle ?? Game.LocalPlayer.Character.LastVehicle;
            if (pv == null) return;
            pv.Model.Load();
            switch (currentStartType)
            {
                case StartType.Drag: SetupDrag(pv); break;
                case StartType.Lined: SetupLined(pv); break;
                case StartType.Staggered: SetupStaggered(pv); break;
            }
        }

        /// <summary>
        /// Finds the floor height using a raycast, requesting collision at coordinates first.
        /// </summary>
        private static Vector3 GetGroundPos(Vector3 pos)
        {
            NativeFunction.Natives.REQUEST_COLLISION_AT_COORD(pos.X, pos.Y, pos.Z);
            int handle = NativeFunction.Natives.START_SHAPE_TEST_CAPSULE<int>(pos.X, pos.Y, pos.Z + 10.0f, pos.X, pos.Y, pos.Z - 10.0f, 1.0f, 1, 0, 7);
            bool hit; Vector3 endPos, surfaceNormal; int entityHit;
            NativeFunction.Natives.GET_SHAPE_TEST_RESULT<int>(handle, out hit, out endPos, out surfaceNormal, out entityHit);
            if (hit) return new Vector3(pos.X, pos.Y, endPos.Z);
            float groundZ;
            if (NativeFunction.Natives.GET_GROUND_Z_FOR_3D_COORD<bool>(pos.X, pos.Y, pos.Z, out groundZ, false)) return new Vector3(pos.X, pos.Y, groundZ);
            return pos;
        }

        /// <summary>
        /// Spawns a preview vehicle and corrects the height based on the model's dimensions.
        /// </summary>
        private static void CreatePreviewVehicle(Model model, Vector3 pos, float heading)
        {
            Vehicle v = new Vehicle(model, pos, heading);
            if (v.Exists())
            {
                v.Opacity = 0.4f;
                v.IsCollisionProof = true;

                // FIX: Calculate Z-offset to prevent sinking. 
                // We add half the model's height to place the bottom of the wheels on the ground.
                Vector3 min, max;
                NativeFunction.Natives.GET_MODEL_DIMENSIONS(model.Hash, out min, out max);
                float zOffset = Math.Abs(min.Z);

                NativeFunction.Natives.SET_ENTITY_COORDS_NO_OFFSET(v, pos.X, pos.Y, pos.Z + zOffset, false, false, false);
                v.Heading = heading;
                NativeFunction.Natives.FREEZE_ENTITY_POSITION(v, true);
                previewVehicles.Add(v);
            }
        }

        private static void SetupDrag(Vehicle pv)
        {
            int frontWidth = 2;
            int total = Math.Min(frontWidth + (frontWidth * rowsBack), MaxStarts);
            for (int i = 0; i < total; i++)
            {
                int row = i / frontWidth; int col = i % frontWidth;
                Vector3 offset = new Vector3((col * sideSpacing) * sideMultiplier, -row * 8.0f, 0f);
                Vector3 pos = GetGroundPos(pv.Position + offset.RotateZ(pv.Heading));
                starts.Add(new StartPos { Position = pos, Heading = pv.Heading });
                if (i > 0) CreatePreviewVehicle(pv.Model, pos, pv.Heading);
            }
        }

        private static void SetupLined(Vehicle pv)
        {
            int total = Math.Min(carsWide + (carsWide * rowsBack), MaxStarts);
            for (int i = 0; i < total; i++)
            {
                int row = i / carsWide; int col = i % carsWide;
                Vector3 offset = new Vector3((col * sideSpacing) * sideMultiplier, -row * 8.0f, 0f);
                Vector3 pos = GetGroundPos(pv.Position + offset.RotateZ(pv.Heading));
                starts.Add(new StartPos { Position = pos, Heading = pv.Heading });
                if (i > 0) CreatePreviewVehicle(pv.Model, pos, pv.Heading);
            }
        }

        private static void SetupStaggered(Vehicle pv)
        {
            int total = Math.Min(carsWide + (carsWide * rowsBack), MaxStarts);
            for (int i = 0; i < total; i++)
            {
                int row = i / carsWide; int col = i % carsWide;
                Vector3 offset = new Vector3((col * sideSpacing) * sideMultiplier, (-row * 8.0f) - (col * 2.0f), 0f);
                Vector3 pos = GetGroundPos(pv.Position + offset.RotateZ(pv.Heading));
                starts.Add(new StartPos { Position = pos, Heading = pv.Heading });
                if (i > 0) CreatePreviewVehicle(pv.Model, pos, pv.Heading);
            }
        }

        /// <summary>
        /// Adds a checkpoint to the track.
        /// </summary>
        private static void AddCheckpoint()
        {
            Vector3 pos = Game.LocalPlayer.Character.Position;
            if (snapToRoad)
            {
                Vector3 roadPos;
                if (NativeFunction.Natives.GET_CLOSEST_VEHICLE_NODE<bool>(pos.X, pos.Y, pos.Z, out roadPos, 1, 3.0f, 0)) pos = roadPos;
            }
            checkpoints.Add(pos);
            previewBlips.Add(new Blip(pos) { Color = Color.DeepSkyBlue });
        }

        /// <summary>
        /// Removes the most recent checkpoint.
        /// </summary>
        private static void RemoveLastCheckpoint()
        {
            if (checkpoints.Count > 0)
            {
                int idx = checkpoints.Count - 1;
                if (previewBlips[idx].Exists()) previewBlips[idx].Delete();
                previewBlips.RemoveAt(idx);
                checkpoints.RemoveAt(idx);
            }
        }

        /// <summary>
        /// Clears all track data.
        /// </summary>
        private static void ClearAll()
        {
            startsLocked = false;
            if (lockItem != null) lockItem.Checked = false;
            ClearPreviewVehicles();
            foreach (Blip b in previewBlips) if (b.Exists()) b.Delete();
            previewBlips.Clear();
            checkpoints.Clear();
            starts.Clear();
        }

        /// <summary>
        /// Deletes all ghost preview vehicles.
        /// </summary>
        private static void ClearPreviewVehicles()
        {
            foreach (Vehicle v in previewVehicles) if (v.Exists()) v.Delete();
            previewVehicles.Clear();
        }

        /// <summary>
        /// Exports the track data to a C# snippet.
        /// </summary>
        private static void ExportCSharp()
        {
            if (checkpoints.Count == 0 && starts.Count == 0) return;
            StringBuilder sb = new StringBuilder();
            string varName = trackName.Replace("_", "").ToLower();
            CultureInfo inv = CultureInfo.InvariantCulture;

            sb.AppendLine($"        List<VehicleRaceStartingPosition> {varName}start = new List<VehicleRaceStartingPosition>()\n        {{");
            List<StartPos> finalOrder = new List<StartPos>(starts);
            if (currentExportOrder == ExportOrder.PlayerLast) finalOrder.Reverse();
            for (int i = 0; i < finalOrder.Count; i++)
            {
                Vector3 p = finalOrder[i].Position;
                sb.AppendLine($"            new VehicleRaceStartingPosition({i}, new Vector3({p.X.ToString("F3", inv)}f, {p.Y.ToString("F3", inv)}f, {p.Z.ToString("F3", inv)}f), {finalOrder[i].Heading.ToString("F3", inv)}f),");
            }
            sb.AppendLine("        };\n");

            sb.AppendLine($"        List<VehicleRaceCheckpoint> {varName}checkpoints = new List<VehicleRaceCheckpoint>()\n        {{");
            for (int i = 0; i < checkpoints.Count; i++)
            {
                Vector3 p = checkpoints[i];
                sb.AppendLine($"            new VehicleRaceCheckpoint({i}, new Vector3({p.X.ToString("F3", inv)}f, {p.Y.ToString("F3", inv)}f, {p.Z.ToString("F3", inv)}f)),");
            }
            sb.AppendLine("        };\n");

            sb.AppendLine($"        VehicleRaceTrack {varName} = new VehicleRaceTrack(\"{trackName}\", \"{trackName.Replace("_", " ")}\", \"Created with LSR Track Creator\", {varName}checkpoints, {varName}start);");
            sb.AppendLine($"        VehicleRaceTypeManager.VehicleRaceTracks.Add({varName});");

            File.WriteAllText("CustomTrackCode_" + trackName + ".txt", sb.ToString());
            Game.DisplayNotification($"~g~Exported:~w~ {trackName}.txt");
        }

        /// <summary>
        /// Helper to draw UI text on the screen.
        /// </summary>
        private static void DrawSimpleText(string text, float x, float y)
        {
            NativeFunction.Natives.SET_TEXT_FONT(4);
            NativeFunction.Natives.SET_TEXT_SCALE(0.5f, 0.5f);
            NativeFunction.Natives.SET_TEXT_OUTLINE();
            NativeFunction.Natives.SET_TEXT_CENTRE(true);
            NativeFunction.Natives.BEGIN_TEXT_COMMAND_DISPLAY_TEXT("STRING");
            NativeFunction.Natives.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME(text);
            NativeFunction.Natives.END_TEXT_COMMAND_DISPLAY_TEXT(x, y);
        }

        public class StartPos { public Vector3 Position { get; set; } public float Heading { get; set; } }
    }

    /// <summary>
    /// Rotates a Vector3 around the Z-axis by a given heading in degrees.
    /// </summary>
    public static class Vector3Extensions
    {
        public static Vector3 RotateZ(this Vector3 v, float h)
        {
            float r = h * (float)Math.PI / 180f;
            float c = (float)Math.Cos(r), s = (float)Math.Sin(r);
            return new Vector3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
        }
    }
}