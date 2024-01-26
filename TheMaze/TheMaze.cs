using UnityEngine;
using System.Collections.Generic;
using HarmonyLib;
using System;
using UnityEngine.AzureSky;
using System.Threading;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using I2.Loc;

public class TheMaze : Mod
{
    public static Maze maze;
    public static GameObject mazeObj;
    static Light endLight;
    static List<GameObject> walls;
    public const float floorHeight = 2.4f;
    public const float cellSize = 1.5f;
    const float wallThickness = 0.1f;
    static List<Tuple<GameObject, bool>> stateChanges;
    static Traverse<SO_GameModeValue[]> gamemodes = Traverse.Create(typeof(GameModeValueManager)).Field<SO_GameModeValue[]>("gameModeValues");
    static bool meshCreated;
    static Thread updateThread;
    Harmony harmony;
    static Button unloadButton;
    static Button.ButtonClickedEvent eventStore = null;
    static bool reachedEnd;
    static bool CanUnload
    {
        get { return eventStore == null; }
        set
        {
            if (!value && eventStore == null)
            {
                eventStore = unloadButton.onClick;
                unloadButton.onClick = new Button.ButtonClickedEvent();
                unloadButton.onClick.AddListener(delegate { Debug.LogError("Mod cannot be unloaded in world"); });
            }
            else if (value && eventStore != null)
            {
                unloadButton.onClick = eventStore;
                eventStore = null;
            }
        }
    }
    public void Start()
    {
        unloadButton = modlistEntry.modinfo.unloadBtn.GetComponent<Button>();
        stateChanges = new List<Tuple<GameObject, bool>>();
        walls = new List<GameObject>();
        harmony = new Harmony("com.aidanamite.TheMaze");
        harmony.PatchAll();
        updateThread = new Thread(new ThreadStart(WallUpdates));
        updateThread.Start();
        List<SO_GameModeValue> gameModeValues = new List<SO_GameModeValue>(gamemodes.Value);
        gameModeValues.Add(CreateMode());
        gamemodes.Value = gameModeValues.ToArray();
        foreach (NewGameBox box in Resources.FindObjectsOfTypeAll<NewGameBox>())
            EditMenu(box);
        if (GameManager.GameMode == (GameMode)6 && SceneManager.GetActiveScene().name == Raft_Network.GameSceneName)
            modlistEntry.jsonmodinfo.requiredByAllPlayers = false;
        Log("Mod has been loaded!");
    }

    public static void EditMenu(NewGameBox box)
    {
        var tabContainer = box.transform.Find("TabContainer");
        var newTab = GameObject.Instantiate(tabContainer.Find("CreativeTab").gameObject, tabContainer, false);
        newTab.name = "MazeTab";
        destroyLocalizations(newTab);
        newTab.GetComponentInChildren<Text>().text = "Maze";
        var rect = newTab.GetComponent<RectTransform>();
        var lastTab = tabContainer.GetChild(tabContainer.childCount - 2).GetComponent<RectTransform>();
        var secondLastTab = tabContainer.GetChild(tabContainer.childCount - 3).GetComponent<RectTransform>();
        float dif = lastTab.anchoredPosition.x - secondLastTab.anchoredPosition.x;
        rect.anchoredPosition = lastTab.anchoredPosition * 2 - secondLastTab.anchoredPosition;
        box.GetComponent<RectTransform>().offsetMax += new Vector2(dif, 0);
        tabContainer.GetComponent<RectTransform>().offsetMax += new Vector2(dif, 0);
        var closeButton = box.transform.Find("CloseButton").GetComponent<RectTransform>();
        closeButton.offsetMax += new Vector2(dif, 0);
        closeButton.offsetMin += new Vector2(dif, 0);
        var newDesc = GameObject.Instantiate(box.transform.Find("Creative_ModeDescription").gameObject, box.transform);
        newDesc.name = "Maze_ModeDescription";
        destroyLocalizations(newDesc);
        newDesc.GetComponent<Text>().text = "A spoopy maze.";
        newTab.GetComponent<TabButtonGameMode>().tabIndex = tabContainer.childCount - 1;
        Traverse.Create(newTab.GetComponent<TabButtonGameMode>()).Field("tabGameMode").SetValue((GameMode)6);
        Traverse.Create(newTab.GetComponent<TabButtonGameMode>()).Field("tab").SetValue(newDesc);
        Traverse.Create(newTab.GetComponent<TabButtonGameMode>()).Field("tabGroup").SetValue(tabContainer.GetComponent<TabGroup>());
        Traverse.Create(newTab.GetComponent<TabButtonGameMode>()).Field("button").SetValue(newTab.GetComponent<Button>());

        var tabButtons = Traverse.Create(tabContainer.GetComponent<TabGroup>()).Field<TabButton[]>("tabButtons");
        if (tabButtons.Value != null)
        {
            var buttons = new List<TabButton>(tabButtons.Value);
            buttons.Add(newTab.GetComponent<TabButtonGameMode>());
            tabButtons.Value = buttons.ToArray();
        }
    }

    public static void destroyLocalizations(GameObject gO)
    {
        foreach (Localize localize in gO.GetComponentsInChildren<Localize>())
        {
            localize.enabled = false;
            GameObject.DestroyImmediate(localize, true);
        }
    }

    public static Vector2 Next(Vector2 a, Vector2 b)
    {
        return a * 2 - b;
    }

    public static void UneditMenu(NewGameBox box)
    {
        var tabContainer = box.transform.Find("TabContainer");
        var tab = tabContainer.Find("MazeTab");
        var desc = box.transform.Find("Maze_ModeDescription");
        Destroy(tab.gameObject);
        Destroy(desc.gameObject);
        var lastTab = tabContainer.GetChild(tabContainer.childCount - 1).GetComponent<RectTransform>();
        var secondLastTab = tabContainer.GetChild(tabContainer.childCount - 2).GetComponent<RectTransform>();
        float dif = lastTab.anchoredPosition.x - secondLastTab.anchoredPosition.x;
        box.GetComponent<RectTransform>().offsetMax -= new Vector2(dif, 0);
        var closeButton = box.transform.Find("CloseButton").GetComponent<RectTransform>();
        closeButton.offsetMax -= new Vector2(dif, 0);
        closeButton.offsetMin -= new Vector2(dif, 0);
        tabContainer.GetComponent<RectTransform>().offsetMax -= new Vector2(dif, 0);
        var tabButtons = Traverse.Create(tabContainer.GetComponent<TabGroup>()).Field<TabButton[]>("tabButtons");
        if (tabButtons.Value == null)
            return;
        var buttons = new List<TabButton>(tabButtons.Value);
        for (int i = buttons.Count - 1; i >= 0; i--)
            if (buttons[i] == null || buttons[i].name == "MazeTab")
                buttons.RemoveAt(i);
        tabButtons.Value = buttons.ToArray();
    }

    public override void WorldEvent_WorldLoaded()
    {
        if (GameManager.GameMode == (GameMode)6)
            CanUnload = false;
        else
            modlistEntry.jsonmodinfo.requiredByAllPlayers = false;
        reachedEnd = false;
    }
    public override void WorldEvent_WorldUnloaded()
    {
        CanUnload = true;
        modlistEntry.jsonmodinfo.requiredByAllPlayers = true;
    }

    public static SO_GameModeValue CreateMode()
    {
        var mode = ScriptableObject.CreateInstance<SO_GameModeValue>();
        mode.gameMode = (GameMode)6;
        mode.achievementVariables = new AchievementVariables() { trackAchievments = false };
        mode.bearVariables = new BearVariables() { damageMultiplier = 1, isTame = false, shouldSpawn = false };
        mode.boarVariables = new BoarVariables() { damageMultiplier = 1, isTame = false, shouldSpawn = false };
        mode.butlerBotVariables = new ButlerBotVariables() { damageMultiplier = 1, isTame = false, shouldSpawn = false };
        mode.domesticVariables = new DomesticVariables() { hungerGain = 0, startHunger = 100, starveTime = 240 };
        mode.enviromentalVariables = new EnvironmentalVariables() { spawnObjects = false };
        mode.itemSpecificVariables = new ItemSpecificVariables() { sharkBaitHealthMultiplier = 1 };
        mode.nourishmentVariables = new NourishmentVariables() { foodDecrementRateMultiplier = 0, thirstDecrementRateMultiplier = 0 };
        mode.playerSpecificVariables = new PlayerSpecificVariables() {
            allowNoclip = false,
            alwaysHealthy = false,
            canSurrender = true,
            clearInventoryOnSurrender = false,
            damageTakenMultiplier = 1,
            giveStartingItems = false,
            learnAllRecipiesAtStart = false,
            negateOutgoingPlayerDamage = false,
            outgoingDamageMultiplierPVE = 1,
            recieveFallDamage = true,
            unlimitedResources = false
        };
        mode.pufferfishVariables = new PufferfishVariables() { damageMultiplier = 1, isTame = false, shouldSpawn = false };
        mode.raftSpecificVariables = new RaftSpecificVariables() { handleRaftCollision = false, isRaftAlwaysAnchored = true, prewarmFloatingItems = false, usesBuoyancy = false };
        mode.ratVariables = new RatVariables() { damageMultiplier = 1, isTame = false, shouldSpawn = false };
        mode.seagullVariables = new SeagullVariables() { attacksCrops = false, damageMultiplier = 1, isTame = false, shouldSpawn = false };
        mode.sharkVariables = new SharkVariables() { spawnRateMultiplier = 0, attackRateMultiplier = 0, damageMultiplier = 1, isTame = false, shouldSpawn = false };
        mode.specialEffectsVariables = new SpecialEffectsVariables() { damageOverTimeMultiplier = 1 };
        mode.stonebirdVariables = new StonebirdVariables() { damageMultiplier = 1, isTame = false, shouldSpawn = false };
        mode.toolVariables = new ToolVariables() { areToolsIndestructible = true, removeSpeedMultiplier = 0, toolDurabilityLossMultiplier = 0 };
        return mode;
    }

    public void Update()
    {
        if (GameManager.GameMode != (GameMode)6)
            return;
        while (stateChanges.Count > 0)
        {
            if (stateChanges[0] != null && stateChanges[0].Item1 != null)
                stateChanges[0].Item1.SetActive(stateChanges[0].Item2);
            stateChanges.RemoveAt(0);
        }
        var player = RAPI.GetLocalPlayer();
        if (!reachedEnd && player != null && endLight != null && Vector3.Distance(player.transform.position, endLight.transform.position) < cellSize / 2)
        {
            ComponentManager<ChatManager>.Value.SendChatMessage(ComponentManager<Settings>.Value.Current.character.Name + " has reached the end of the maze!", new Steamworks.CSteamID(0));
            reachedEnd = true;
        }
    }

    public static void WallUpdates()
    {
        while (true)
        {
            Thread.Sleep(200);
            var Player = RAPI.GetLocalPlayer();
            if (Player != null && mazeObj != null && meshCreated)
                foreach (GameObject wall in walls)
                    if (wall.activeSelf != Vector3.Distance(wall.transform.position, Player.transform.position) < 8)
                        stateChanges.Add(new Tuple<GameObject, bool>(wall, !wall.activeSelf));
        }
    }

    public static void placeWalls(Maze maze) => placeWalls(maze.cells);
    public static void placeWalls(MazeCell[,,] cells) {
        Material grass = null;
        foreach (MeshRenderer m in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            if (m.name == "GrassPlotModel" && !m.material.name.Contains("Ghost"))
            {
                grass = m.material;
                break;
            }
        Dictionary<string, Mesh> varients = new Dictionary<string, Mesh>();
        foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>())
        {
            if (m.name == "GrassPlot_Single")
                varients.Add("tttt", m);
            if (m.name == "GrassPlot_Ending")
                varients.Add("tttf", m);
            if (m.name == "GrassPlot_Corner")
                varients.Add("ttff", m);
            if (m.name == "GrassPlot_MiddleWSides")
                varients.Add("tftf", m);
            if (m.name == "GrassPlot_Side")
                varients.Add("tfff", m);
            if (m.name == "GrassPlot_Middle")
                varients.Add("ffff", m);
        }
        BoxCollider normalCollider = null;
        BoxCollider climbCollider = null;
        foreach (Block block in Resources.FindObjectsOfTypeAll<Block>())
            if (block.name == "Block_Ladder")
            {
                normalCollider = block.onoffColliders[0] as BoxCollider;
                climbCollider = block.onoffColliders[1] as BoxCollider;
            }
        meshCreated = false;
        walls.Clear();
        mazeObj = new GameObject("TheMaze");
        mazeObj.transform.position = ComponentManager<Raft>.Value.transform.Find("RotatePivot").Find("LockedPivot").position + new Vector3(0, floorHeight * 10, 0);
        foreach (MazeCell cell in cells)
        {
            foreach (Wall wall in cell.Walls())
            {
                var n = new GameObject("TheMaze_WallObject");
                var design = maze.getWallDesign(cell, wall);
                n.AddComponent<MeshRenderer>().material = grass;
                n.AddComponent<MeshFilter>().mesh = varients[design.Item1];
                n.transform.SetParent(mazeObj.transform, false);
                n.SetActive(false);
                if (wall == Wall.North)
                {
                    n.transform.localPosition = new Vector3(cell.Location.x * cellSize, (cell.Location.y + 0.5f) * floorHeight, (cell.Location.z + 0.5f) * cellSize - wallThickness / 2);
                    n.transform.localRotation = Quaternion.Euler(90, 0, 0);
                    n.transform.localScale = new Vector3(1, 0.5f, 1 / cellSize * floorHeight);

                    if (cell.Location.z != cells.GetUpperBound(2))
                    {
                        var w = Instantiate(normalCollider);
                        w.name = "TheMaze_WallCollider";
                        w.gameObject.layer = 16;
                        w.transform.SetParent(n.transform, false);
                        w.size = new Vector3(cellSize, wallThickness, cellSize);
                        w.transform.localPosition = Vector3.zero;
                        w.gameObject.SetActive(true);
                        w.enabled = true;
                    }
                }
                else if (wall == Wall.East)
                {
                    n.transform.localPosition = new Vector3((cell.Location.x + 0.5f) * cellSize - wallThickness / 2, (cell.Location.y + 0.5f) * floorHeight, cell.Location.z * cellSize);
                    n.transform.localRotation = Quaternion.Euler(90, 90, 0);
                    n.transform.localScale = new Vector3(1, 0.5f, 1 / cellSize * floorHeight);
                    if (cell.Location.x != cells.GetUpperBound(0))
                    {
                        var w = Instantiate(normalCollider);
                        w.name = "TheMaze_WallCollider";
                        w.gameObject.layer = 16;
                        w.transform.SetParent(n.transform, false);
                        w.size = new Vector3(cellSize, wallThickness, cellSize);
                        w.transform.localPosition = Vector3.zero;
                        w.gameObject.SetActive(true);
                        w.enabled = true;
                    }
                }
                else if (wall == Wall.South)
                {
                    n.transform.localPosition = new Vector3(cell.Location.x * cellSize, (cell.Location.y + 0.5f) * floorHeight, (cell.Location.z - 0.5f) * cellSize + wallThickness / 2);
                    n.transform.localRotation = Quaternion.Euler(90, 180, 0);
                    n.transform.localScale = new Vector3(1, 0.5f, 1 / cellSize * floorHeight);

                }
                else if (wall == Wall.West)
                {
                    n.transform.localPosition = new Vector3((cell.Location.x - 0.5f) * cellSize + wallThickness / 2, (cell.Location.y + 0.5f) * floorHeight, cell.Location.z * cellSize);
                    n.transform.localRotation = Quaternion.Euler(90, 270, 0);
                    n.transform.localScale = new Vector3(1, 0.5f, 1 / cellSize * floorHeight);
                }
                else if (wall == Wall.Up)
                {
                    n.transform.localPosition = new Vector3(cell.Location.x * cellSize, (cell.Location.y + 1) * floorHeight - wallThickness / 2, cell.Location.z * cellSize);
                    n.transform.localRotation = Quaternion.Euler(0, 0, 0);
                    n.transform.localScale = new Vector3(1, 0.5f, 1);
                    if (cell.Location.y != cells.GetUpperBound(1))
                    {
                        var w = Instantiate(normalCollider);
                        w.name = "TheMaze_WallCollider";
                        w.gameObject.layer = 16;
                        w.transform.SetParent(n.transform, false);
                        w.size = new Vector3(cellSize, wallThickness, cellSize);
                        w.transform.localPosition = Vector3.zero;
                        w.gameObject.SetActive(true);
                        w.enabled = true;
                    }
                }
                else if (wall == Wall.Down)
                {
                    n.transform.localPosition = new Vector3(cell.Location.x * cellSize, cell.Location.y * floorHeight + wallThickness / 2, cell.Location.z * cellSize);
                    n.transform.localRotation = Quaternion.Euler(180, 0, 0);
                    n.transform.localScale = new Vector3(1, 0.5f, 1);
                }
                Vector3 scale = n.transform.TransformDirection(n.transform.localScale);
                n.transform.Rotate(Vector3.up, 90 * design.Item2);
                n.transform.localScale = n.transform.InverseTransformDirection(scale).Abs();
                walls.Add(n);
            }
            if (!cell.Contains(Wall.Up))
            {
                var c = Instantiate(climbCollider);
                c.name = "TheMaze_Climb";
                c.gameObject.layer = 16;
                c.transform.SetParent(mazeObj.transform, false);
                c.size = new Vector3(cellSize * 0.75f, floorHeight, cellSize * 0.75f);
                c.transform.localPosition = new Vector3(cell.Location.x * cellSize, (cell.Location.y + 0.5f) * floorHeight, cell.Location.z * cellSize);
                c.transform.localRotation = Quaternion.Euler(0, 0, 0);
                c.gameObject.SetActive(true);
                c.enabled = true;
            }
        }
        for (int i = 0; i < 6; i++)
        {
            var edge = Instantiate(normalCollider);
            edge.name = "TheMaze_Edge";
            edge.gameObject.layer = 16;
            edge.transform.SetParent(mazeObj.transform, false);
            edge.size = new Vector3(cellSize, 1, cellSize);
            edge.transform.localPosition = new Vector3(
                (i == 3) ? cellSize * cells.Size(0) - cellSize / 2 : ((i == 5) ? -cellSize / 2 : (cellSize * (cells.Size(0) - 1) / 2)),
                (i == 0) ? 0 : ((i == 1) ? floorHeight * cells.Size(1) : (floorHeight * cells.Size(1) / 2)),
                (i == 2) ? cellSize * cells.Size(2) - cellSize / 2 : ((i == 4) ? -cellSize / 2 : (cellSize * (cells.Size(2) - 1) / 2))
            );
            edge.transform.localRotation = Quaternion.Euler((i == 0) ? 0 : (i == 1) ? 180 : 90, (i < 2) ? 0 : (i * 90), 0);
            edge.transform.localScale = new Vector3(cells.Size((i == 3 || i == 5) ? 2 : 0), wallThickness, (i < 2) ? cells.Size(2) : (cells.Size(1) / cellSize * floorHeight));
            edge.gameObject.SetActive(true);
            edge.enabled = true;
            edge.gameObject.AddComponent<MeshRenderer>().material = grass;
            edge.gameObject.AddComponent<MeshFilter>().mesh = varients["ffff"];
            var inner = new GameObject("TheMaze_InnerEdge");
            inner.transform.SetParent(edge.transform, false);
            inner.transform.localRotation = Quaternion.Euler(180, 0, 0);
            inner.SetActive(true);
            inner.AddComponent<MeshRenderer>().material = grass;
            inner.AddComponent<MeshFilter>().mesh = varients["ffff"];
        }
        endLight = new GameObject("TheMaze_End").AddComponent<Light>();
        endLight.color = Color.green;
        endLight.intensity = 3;
        endLight.range = 5;
        endLight.transform.SetParent(mazeObj.transform, false);
        endLight.transform.position = GetLocationOfCell(30, 30.5f, 59);
        endLight.enabled = true;
        endLight.gameObject.SetActive(true);
        meshCreated = true;
    }

    public static void generateMazeData()
    {
        maze = new Maze(60,60,60);
        maze.generate(new Location[] {
            new Location(29, 30, 29),
            new Location(30, 30, 29),
            new Location(29, 30, 30),
            new Location(30, 30, 30)
        });
    }

    [ConsoleCommand(name: "cell", docs: "")]
    public static string MyCommand(string[] args)
    {
        if (args.Length == 0)
        {
            Vector3 p = RAPI.GetLocalPlayer().transform.position - mazeObj.transform.position;
            return Math.Round(p.x / cellSize) + ", " + Math.Round(p.y / floorHeight) + ", " + Math.Round(p.z / cellSize);
        }
        RAPI.GetLocalPlayer().transform.position = GetLocationOfCell(Int32.Parse(args[0]), Int32.Parse(args[1]) + 0.5f, Int32.Parse(args[2]));
        return "Warped";
    }

    public void OnModUnload()
    {
        foreach (NewGameBox box in Resources.FindObjectsOfTypeAll<NewGameBox>())
            if (box.transform.Find("TabContainer").Find("MazeTab") != null)
                UneditMenu(box);
        List<SO_GameModeValue> gameModeValues = new List<SO_GameModeValue>(gamemodes.Value);
        for (int i = 0; i < gameModeValues.Count; i++)
            if (gameModeValues[i].gameMode == (GameMode)6)
            {
                gameModeValues.RemoveAt(i);
                break;
            }
        gamemodes.Value = gameModeValues.ToArray();
        updateThread.Abort();
        if (SceneManager.GetActiveScene().name == Raft_Network.MenuSceneName)
            FindObjectOfType<AzureSkyController>().timeOfDay.GotoTime(9.6f);
        harmony.UnpatchAll(harmony.Id);
        Log("Mod has been unloaded!");
    }

    public static Vector3 GetLocationOfCell(float X, float Y, float Z)
    {
        return mazeObj.transform.position + new Vector3(X * cellSize, Y * floorHeight, Z * cellSize);
    }
}

static class ExtenstionMethods
{
    public static Wall other(this Wall wall)
    {
        if (wall == Wall.North)
            return Wall.South;
        if (wall == Wall.South)
            return Wall.North;
        if (wall == Wall.East)
            return Wall.West;
        if (wall == Wall.West)
            return Wall.East;
        if (wall == Wall.Up)
            return Wall.Down;
        if (wall == Wall.Down)
            return Wall.Up;
        return Wall.None;
    }
    public static Wall up(this Wall wall)
    {
        if (wall == Wall.North || wall == Wall.South || wall == Wall.East || wall == Wall.West)
            return Wall.Up;
        if (wall == Wall.Down)
            return Wall.North;
        if (wall == Wall.Up)
            return Wall.South;
        return Wall.None;
    }
    public static Wall down(this Wall wall)
    {
        if (wall == Wall.North || wall == Wall.South || wall == Wall.East || wall == Wall.West)
            return Wall.Down;
        if (wall == Wall.Down)
            return Wall.South;
        if (wall == Wall.Up)
            return Wall.North;
        return Wall.None;
    }
    public static Wall left(this Wall wall)
    {
        if (wall == Wall.North || wall == Wall.Down || wall == Wall.Up)
            return Wall.West;
        if (wall == Wall.South)
            return Wall.East;
        if (wall == Wall.East)
            return Wall.North;
        if (wall == Wall.West)
            return Wall.South;
        return Wall.None;
    }
    public static Wall right(this Wall wall)
    {
        if (wall == Wall.North || wall == Wall.Down || wall == Wall.Up)
            return Wall.East;
        if (wall == Wall.South)
            return Wall.West;
        if (wall == Wall.East)
            return Wall.South;
        if (wall == Wall.West)
            return Wall.North;
        return Wall.None;
    }

    public static Vector3 Abs(this Vector3 vector)
    {
        return new Vector3(Math.Abs(vector.x), Math.Abs(vector.y), Math.Abs(vector.z));
    }

    public static string Serialize(this int value)
    {
        return "" + (char)(value % 65536) + (char)(value / 65536);
    }

    public static int Deserialize(this string value, int index)
    {
        return value[index] + value[index + 1] * 65536;
    }

    public static int Size(this Array array, int Dimension)
    {
        return array.GetUpperBound(Dimension) + 1;
    }
}

public class Maze
{
    public MazeCell[,,] cells;
    List<Location> workingCells;
    public Maze(int xSize, int ySize, int zSize) : this(new MazeCell[xSize, ySize, zSize]) { }
    public Maze(MazeCell[,,] Cells)
    {
        workingCells = new List<Location>();
        cells = Cells;
        for (int x = 0; x < cells.Size(0); x++)
            for (int y = 0; y < cells.Size(1); y++)
                for (int z = 0; z < cells.Size(2); z++)
                {
                    cells[x, y, z].Location.Set(x, y, z);
                    cells[x, y, z] += (Wall)63;
                }
    }
    public Maze(string serial)
    {
        workingCells = new List<Location>();
        cells = new MazeCell[serial.Deserialize(0), serial.Deserialize(2), serial.Deserialize(4)];
        int x = 0;
        int y = 0;
        int z = 0;
        var rawData = new byte[serial.Length * 2 - 12];
        for (int i = 0; i < rawData.Length / 2; i++)
        {
            int j = serial[i + 6];
            rawData[i * 2] = (byte)(j % 256);
            rawData[i * 2 + 1] = (byte)(j / 256);
        }
        for (int i = 0; i < rawData.Length; i += 3)
        {
            bool[] tempData = new bool[24];
            for (int j = 0; j < 3; j++)
            {
                byte k = rawData[i + j];
                int l = 0;
                while (k > 0)
                {
                    tempData[j * 8 + l] = k % 2 == 1;
                    k /= 2;
                    l++;
                }
            }
            for (int j = 0; j < tempData.Length / 3; j++)
            {
                if (tempData[j * 3])
                    cells[x, y, z] += Wall.North;
                if (tempData[j * 3 + 1])
                    cells[x, y, z] += Wall.East;
                if (tempData[j * 3 + 2])
                    cells[x, y, z] += Wall.Up;
                if (x == 0)
                    cells[x, y, z] += Wall.West;
                if (y == 0)
                    cells[x, y, z] += Wall.Down;
                if (z == 0)
                    cells[x, y, z] += Wall.South;
                cells[x, y, z].Location = new Location(x, y, z);
                if (tempData[j * 3] && inBounds(cells[x, y, z].Location + Wall.North))
                    this[cells[x, y, z].Location + Wall.North] += Wall.South;
                if (tempData[j * 3 + 1] && inBounds(cells[x, y, z].Location + Wall.East))
                    this[cells[x, y, z].Location + Wall.East] += Wall.West;
                if (tempData[j * 3 + 2] && inBounds(cells[x, y, z].Location + Wall.Up))
                    this[cells[x, y, z].Location + Wall.Up] += Wall.Down;
                if (x == cells.GetUpperBound(0) && y == cells.GetUpperBound(1) && z == cells.GetUpperBound(2))
                    return;
                z++;
                if (z == cells.Size(0))
                {
                    y++;
                    z = 0;
                }
                if (y == cells.Size(1))
                {
                    x++;
                    y = 0;
                }
            }
        }
    }
    public void generate(Location[] startCells)
    {
        if (startCells.Length == 0)
            return;
        var start = new List<Location>(startCells);
        workingCells.Add(startCells[0]);
        start.RemoveAt(0);
        int changes = 1;
        while (changes > 0)
        {
            changes = 0;
            for (int i = start.Count - 1; i >= 0; i--)
                foreach (Location location in workingCells)
                    if (start[i].IsNeighbor(location))
                    {
                        workingCells.Add(start[i]);
                        start.RemoveAt(i);
                        changes++;
                        break;
                    }
        }
        foreach (Location l1 in workingCells)
            foreach (Location l2 in workingCells)
                if (l1.IsNeighbor(l2))
                {
                    if (l1.x > l2.x)
                        this[l1] -= Wall.West;
                    else if (l1.x < l2.x)
                        this[l1] -= Wall.East;
                    else if (l1.y > l2.y)
                        this[l1] -= Wall.Down;
                    else if (l1.y < l2.y)
                        this[l1] -= Wall.Up;
                    else if (l1.z > l2.z)
                        this[l1] -= Wall.South;
                    else if (l1.z < l2.z)
                        this[l1] -= Wall.North;
                }
        for (int i = workingCells.Count - 1; i >= 0; i--)
            if (this[workingCells[i]].Empty())
                workingCells.RemoveAt(i);
        while (workingCells.Count > 0)
        {
            int i = UnityEngine.Random.Range(0, workingCells.Count - 1);
            workingCells.AddRange(operate(workingCells[i]));
            workingCells.RemoveAt(i);
        }
    }

    public MazeCell this[Location index]
    {
        get
        {
            return cells[index.x, index.y, index.z];
        }
        set
        {
            cells[index.x, index.y, index.z] = value;
        }
    }

    List<Location> operate(Location loc)
    {
        MazeCell cell = this[loc];
        Wall[] walls = removeInvalid(loc, cell.Walls());
        List<Location> newLocs = new List<Location>();
        if (walls.Length == 0)
            return newLocs;
        foreach (Wall wall in getRandom(walls))
        {
            Location loc2 = loc + wall;
            newLocs.Add(loc2);
            this[loc2] -= wall.other();
            this[loc] -= wall;
        }
        return newLocs;
    }

    Wall[] removeInvalid(Location location, Wall[] walls)
    {
        if (walls.Length == 0)
            return walls;
        List<Wall> valids = new List<Wall>(walls);
        for (int i = valids.Count - 1; i >= 0; i--)
            if (!inBounds(location + valids[i]) || !this[location + valids[i]].Full())
                valids.RemoveAt(i);
        return valids.ToArray();
    }

    bool inBounds(Location location) =>
        location.x >= 0 && location.x < cells.Size(0) &&
        location.y >= 0 && location.y < cells.Size(1) &&
        location.z >= 0 && location.z < cells.Size(2);

    List<Wall> getRandom(Wall[] walls)
    {
        if (walls.Length <= 2)
            return new List<Wall>(walls);
        var nWalls = new List<Wall>();
        int gen = UnityEngine.Random.Range(1, (int)Mathf.Pow(2, walls.Length) - 1);
        for (int i = 0; i < walls.Length; i++)
            if ((gen & (int)Mathf.Pow(2, i)) > 0)
                nWalls.Add(walls[i]);
        return nWalls;
    }

    public Tuple<string,int> getWallDesign(MazeCell cell, Wall wall)
    {
        try
        {
            bool flag1 = cell.Contains(wall.up()) || !this[cell.Location + wall.up()].Contains(wall);
            bool flag2 = cell.Contains(wall.right()) || !this[cell.Location + wall.right()].Contains(wall);
            bool flag3 = cell.Contains(wall.down()) || !this[cell.Location + wall.down()].Contains(wall);
            bool flag4 = cell.Contains(wall.left()) || !this[cell.Location + wall.left()].Contains(wall);
            int sides = (flag1 ? 1 : 0) + (flag2 ? 1 : 0) + (flag3 ? 1 : 0) + (flag4 ? 1 : 0);
            if (sides == 4)
                return new Tuple<string, int>("tttt", 0);
            if (sides == 0)
                return new Tuple<string, int>("ffff", 0);
            if (sides == 3)
                return new Tuple<string, int>("tttf", flag4 ? (flag1 ? (flag2 ? 0 : 1) : 2) : 3);
            if (sides == 1)
                return new Tuple<string, int>("tfff", flag1 ? 0 : (flag2 ? 3 : (flag3 ? 2 : 1)));
            if ((flag1 && flag3) || (flag2 && flag4))
                return new Tuple<string, int>("tftf", flag1 ? 0 : 1);
            return new Tuple<string, int>("ttff", (flag1 && flag2) ? 0 : ((flag2 && flag3) ? 3 : ((flag3 && flag4) ? 2 : 1)));
        } catch
        {
            return new Tuple<string, int>("tttt", 0);
        }
    }

    public string Serialize()
    {
        string data = cells.Size(0).Serialize() + cells.Size(1).Serialize() + cells.Size(2).Serialize();
        bool[] tempData = new bool[48];
        int i = 0;
        for (int x = 0; x < cells.Size(0); x++)
            for (int y = 0; y < cells.Size(0); y++)
                for (int z = 0; z < cells.Size(0); z++)
                {
                    tempData[i * 3] = cells[x, y, z].Contains(Wall.North);
                    tempData[i * 3 + 1] = cells[x, y, z].Contains(Wall.East);
                    tempData[i * 3 + 2] = cells[x, y, z].Contains(Wall.Up);
                    i++;
                    if(i == tempData.Length / 3)
                    {
                        i = 0;
                        data += Serialize(tempData);
                        tempData = new bool[tempData.Length];
                    }
                }
        if (i > 0)
            data += Serialize(tempData);
        return data;
    }

    public static string Serialize(bool[] values)
    {
        string str = "";
        for (int j = 0; j < values.Length; j += 16)
        {
            int k = 0;
            for (int i = 0; i < 16; i++)
                if (values[j + i])
                    k += (int)Math.Pow(2, i);
            str += (char)k;
        }
        return str;
    }
}

public struct MazeCell
{
    int walls;
    public Location Location;
    public bool Contains(Wall wall) => (walls & (int)wall) > 0;
    public bool TryAdd(Wall wall)
    {
        if (Contains(wall))
            return false;
        walls += (int)wall;
        return true;
    }
    public static MazeCell operator +(MazeCell cell, Wall wall)
    {
        cell.walls |= (int)wall;
        return cell;
    }
    public bool TryRemove(Wall wall)
    {
        if (!Contains(wall))
            return false;
        walls -= (int)wall;
        return true;
    }
    public static MazeCell operator -(MazeCell cell, Wall wall) {
        cell.walls ^= cell.walls & (int)wall;
        return cell;
    }
    public bool Full() => walls == 63;
    public bool Empty() => walls == 0;
    public Wall[] Walls()
    {
        List<Wall> walls = new List<Wall>();
        Wall cur = (Wall)1;
        while((int)cur <= this.walls)
        {
            if (Contains(cur))
                walls.Add(cur);
            cur = (Wall)((int)cur * 2);
        }
        return walls.ToArray();
    }

    public int GetWalls()
    {
        return walls;
    }
}

public struct Location
{
    public int x;
    public int y;
    public int z;
    public Location(int X, int Y, int Z)
    {
        x = X;
        y = Y;
        z = Z;
    }
    public void Set(int X, int Y, int Z)
    {
        x = X;
        y = Y;
        z = Z;
    }
    public bool IsNeighbor(Location location)
    {
        int diff = 0;
        if (x > location.x)
            diff += x - location.x;
        else
            diff += location.x - x;
        if (y > location.y)
            diff += y - location.y;
        else
            diff += location.y - y;
        if (z > location.z)
            diff += z - location.z;
        else
            diff += location.z - z;
        return diff == 1;
    }
    public static Location operator +(Location location, Wall wall)
    {
        if ((wall & Wall.North) == Wall.North)
            location.z += 1;
        if ((wall & Wall.East) == Wall.East)
            location.x += 1;
        if ((wall & Wall.South) == Wall.South)
            location.z -= 1;
        if ((wall & Wall.West) == Wall.West)
            location.x -= 1;
        if ((wall & Wall.Up) == Wall.Up)
            location.y += 1;
        if ((wall & Wall.Down) == Wall.Down)
            location.y -= 1;
        return location;
    }
}
public enum Wall
{
    None = 0,
    North = 1,
    East = 2,
    South = 4,
    West = 8,
    Up = 16,
    Down = 32
}

[HarmonyPatch(typeof(AzureSkyController), "Update")]
public class Patch_SkyUpdate
{
    static void Prefix(AzureSkyController __instance)
    {
        if (GameManager.GameMode == (GameMode)6)
            __instance.timeOfDay.GotoTime(0);
        else if (SceneManager.GetActiveScene().name == Raft_Network.MenuSceneName)
            __instance.timeOfDay.GotoTime(9.6f);
        
    }
    static void Postfix(AzureSkyController __instance)
    {
        if (GameManager.GameMode != (GameMode)6)
            return;
        __instance.fogDensity = 1;
        __instance.fogDistance = 5;
        __instance.fogBlend = 0;
        __instance.fogScale = 1;
    }
}

[HarmonyPatch(typeof(BlockCreator), "CreateNewGameLayout")]
public class Patch_CreateStarterRaft
{
    static void Postfix(BlockCreator __instance, Transform ___lockedBuildPivot, ref List<Block> ___placedBlocks)
    {
        if (GameManager.GameMode != (GameMode)6)
            return;
        TheMaze.generateMazeData();
        TheMaze.placeWalls(TheMaze.maze);
        Block blockPrefab = ItemManager.GetItemByName("Placeable_Storage_Medium").settings_buildable.GetBlockPrefab(DPS.Default);
        Block block = UnityEngine.Object.Instantiate(blockPrefab, Vector3.zero, blockPrefab.transform.rotation, ___lockedBuildPivot);
        block.transform.localPosition = Vector3.zero;
        block.OnStartingPlacement();
        ___placedBlocks.Add(block);
        block.ObjectIndex = SaveAndLoad.GetUniqueObjectIndex();
        if (block.networkedBehaviour != null)
        {
            NetworkUpdateManager.SetIndexForBehaviour(block.networkedBehaviour);
            NetworkUpdateManager.AddBehaviour(block.networkedBehaviour);
        }
        block.OnFinishedPlacement();
        if (BlockCreator.PlaceBlockCallStack != null)
        {
            BlockCreator.PlaceBlockCallStack(block, __instance.GetPlayerNetwork(), true, -1);
        }
        (block as Storage_Small).GetInventoryReference().AddItem(new ItemInstance(ItemManager.GetItemByName("Plank"), 1, 1, TheMaze.maze.Serialize()));
    }
}

[HarmonyPatch(typeof(SaveAndLoad), "RestoreRGDGame")]
public class Patch_LoadRGDGame
{
    public static void Postfix()
    {
        if (GameManager.GameMode == (GameMode)6)
            foreach (Storage_Small storage in StorageManager.allStorages)
                foreach (Slot slot in storage.GetInventoryReference().allSlots)
                    if (slot.HasValidItemInstance() && slot.itemInstance.exclusiveString != "")
                    {
                        TheMaze.maze = new Maze(slot.itemInstance.exclusiveString);
                        TheMaze.placeWalls(TheMaze.maze);
                        return;
                    }
    }
}

[HarmonyPatch(typeof(NewGameBox), "Open")]
public class Patch_NewGameBox_Open
{
    static void Prefix(NewGameBox __instance)
    {
        if (__instance.transform.Find("TabContainer").Find("MazeTab") == null)
            TheMaze.EditMenu(__instance);
    }
}

[HarmonyPatch(typeof(BedManager), "Update")]
public class Patch_BedManager
{
    static void Prefix()
    {
        if (BedManager.RespawnPointBed != null && TheMaze.mazeObj != null)
        {
            if (BedManager.RespawnPointBed.transform.parent != TheMaze.mazeObj.transform)
                BedManager.RespawnPointBed.transform.SetParent(TheMaze.mazeObj.transform);
            BedManager.RespawnPointBed.transform.localPosition = TheMaze.GetLocationOfCell(TheMaze.maze.cells.GetUpperBound(0) / 2f, TheMaze.maze.cells.Size(1) / 2f + 0.5f, TheMaze.maze.cells.GetUpperBound(2) / 2f);
        }
    }
}

[HarmonyPatch(typeof(PlayerInventory), "GiveStartingItems")]
public class Patch_SpawnNewPlayer
{
    static void Prefix(Network_Player ___playerNetwork)
    {
        if (GameManager.GameMode != (GameMode)6)
            return;
        if (!Raft_Network.IsHost)
            Patch_LoadRGDGame.Postfix();
        if (___playerNetwork == null)
            ___playerNetwork = ComponentManager<Network_Player>.Value;
        ___playerNetwork.transform.position = TheMaze.GetLocationOfCell(TheMaze.maze.cells.GetUpperBound(0) / 2f, TheMaze.maze.cells.Size(1) / 2f + 0.5f, TheMaze.maze.cells.GetUpperBound(2) / 2f);

    }
}

[HarmonyPatch(typeof(SaveAndLoad), "RestoreUser")]
public class Patch_LoadPlayerRemote
{
    static void Postfix()
    {
        if (GameManager.GameMode == (GameMode)6 && !Raft_Network.IsHost)
            Patch_LoadRGDGame.Postfix();
    }
}

[HarmonyPatch(typeof(SkyManager), "OnRegionUpdate")]
public class Patch_SkyRegionUpdate
{
    static bool changed = false;
    static bool Prefix(ref EnvironmentProbe_ColorGroup ___lastCalculatedEnvironmentColorGroup)
    {
        if (GameManager.GameMode != (GameMode)6)
            return true;
        SkyManager.UsingCustomEnvironmentLight = true;
        if (!changed || ___lastCalculatedEnvironmentColorGroup == null)
        {
            ___lastCalculatedEnvironmentColorGroup = new EnvironmentProbe_ColorGroup(Color.black,Color.black,Color.black,1,1);
            changed = true;
        }
        return false;
    }
}

[HarmonyPatch(typeof(Helper), "GetTerm")]
public class Patch_GetTranslation
{
    static bool Prefix(string term, ref string __result)
    {
        if (term == "Difficulty/6")
        {
            __result = "Maze";
            return false;
        }
        return true;
    }
}