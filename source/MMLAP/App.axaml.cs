using Archipelago.Core;
using Archipelago.Core.AvaloniaGUI.Models;
using Archipelago.Core.AvaloniaGUI.ViewModels;
using Archipelago.Core.AvaloniaGUI.Views;
using Archipelago.Core.Helpers;
using Archipelago.Core.Models;
using Archipelago.Core.Util;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Models;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MMLAP.Helpers;
using MMLAP.Models;
using Newtonsoft.Json;
using ReactiveUI;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Timers;
using static MMLAP.Models.MMLEnums;

namespace MMLAP;

public partial class App : Application
{
    // TODO: Remember to set this in MMLAP.Desktop as well.
    public static readonly string Version = "0.2.0";
    public static readonly List<string> SupportedVersions = ["0.2.0"];

    public static MainWindowViewModel? Context;
    public static ArchipelagoClient? APClient { get; set; }
    private static Dictionary<ushort, LevelData> LevelDataDict { get; set; } = LocationHelpers.GetLevelDataDict();
    private static Dictionary<long, ItemData> ItemDataDict { get; set; } = LocationHelpers.GetItemDataDict();
    private static Dictionary<int, LocationData> LocationDataDict { get; set; } = LocationHelpers.GetLocationDataDict();
    private static Dictionary<long, ItemData>? ScoutedLocationItemData { get; set; }
    private static List<ILocation>? GameLocations { get; set; }
    private static string? PlayerName { get; set; }
    private static bool HasSubmittedGoal { get; set; } = false;
    private static Timer? GameLoopTimer { get; set; }
    private static Timer? StartMMLTimer { get; set; }
    private static TextData? OverwrittenTextData { get; set; }
    private static ushort? PreviousLevelID { get; set; }
    private static bool IsManagingLevelChange { get; set; } = false;
    private static bool IsPreviouslyInTitleScreen { get; set; } = false;
    private static bool IsReceivingItemsAfterLoad { get; set; } = false;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        return;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Start();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Context
            };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainWindow
            {
                DataContext = Context
            };
        }
        base.OnFrameworkInitializationCompleted();
        return;
    }

    private static bool IsRunningAsAdministrator()
    {
        WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Start()
    {
        Context = new MainWindowViewModel("0.6.3 or later");
        Context.ClientVersion = Assembly.GetEntryAssembly().GetName().Version.ToString();

        Context.ConnectClicked += Context_ConnectClicked;
        Context.CommandReceived += Context_CommandReceived;
        Context.OverlayEnabled = true;
        Context.AutoscrollEnabled = true;
        Context.ConnectButtonEnabled = true;

        HasSubmittedGoal = false;

        Log.Logger.Information("This Archipelago Client is compatible only with the NTSC-U release of Mega Man Legends.");
        Log.Logger.Information("Trying to play with a different version will not work as intended.");
        if (!IsRunningAsAdministrator())
        {
            Log.Logger.Warning("You do not appear to be running this client as an administrator.");
            Log.Logger.Warning("This may result in errors or crashes when trying to connect to Duckstation.");
        }
        Log.Logger.Information("Please report any issues in the Discord thread. Thank you!");
        return;
    }

    private void Context_CommandReceived(object? sender, ArchipelagoCommandEventArgs a)
    {

        if (string.IsNullOrWhiteSpace(a.Command))
        {
            return;
        }
        string command = a.Command.Trim().ToLower();
        switch (command)
        {
            case "!help":
                Log.Logger.Information($"> {a.Command}");
                Log.Logger.Information("Available commands:");
                Log.Logger.Information("!help - Show this help message.");
                Log.Logger.Information("!reload - Force reload all items.  Use this if you think you may have missed received items.  Please reconnect to the server while in game to refresh received items.");
                Log.Logger.Information("!goal - Check your current goal.");
                break;
            case "!reload":
                Log.Logger.Information($"> {a.Command}");
                if (APClient != null && APClient.ItemManager != null)
                {
                    Log.Logger.Information("Clearing the game state.  Please reconnect to the server while in game to refresh received items.");
                    APClient.ItemManager.ForceReloadAllItems();
                    return;
                }
                else
                {
                    Log.Logger.Warning("Please connect the client before attempting reload.");
                }
                break;
            case "!goal":
                Log.Logger.Information($"> {a.Command}");
                string goalText;
                if (APClient != null && APClient.Options != null && APClient.Options.TryGetValue("goal", out var goalValueObj))
                {
                    int goalValue = goalValueObj as int? ?? 0;
                    CompletionGoal goal = (CompletionGoal)goalValue;
                    goalText = goal switch
                    {
                        CompletionGoal.Juno => "Defeat Juno",
                        _ => "Unknown",
                    };
                }
                else
                {
                    goalText = "Unknown";
                }
                Log.Logger.Information($"Your goal is: {goalText}.");
                break;
            default:
                APClient?.SendMessage(a.Command);
                break;
        }
        return;
    }

    private async void Context_ConnectClicked(object? sender, ConnectClickedEventArgs e)
    {
        Context.ConnectButtonEnabled = false;
        Log.Logger.Information("Connecting..."); 

        // Refreshing subscriptions
        if (APClient != null)
        {
            APClient.Connected -= Client_Connected;
            APClient.Disconnected -= Client_Disconnected;
            APClient.MessageReceived -= Client_MessageReceived;
            if (APClient.ItemManager != null)
            {
                APClient.ItemManager.ItemReceived -= ItemManager_ItemReceived;
            }
            if (APClient.LocationManager != null)
            {
                APClient.LocationManager.CancelMonitors();
                APClient.LocationManager.EnableLocationsCondition = null;
                APClient.LocationManager.LocationCompleted -= LocationManager_LocationCompleted;
            }
            if (APClient.CurrentSession != null)
            {
                APClient.CurrentSession.Locations.CheckedLocationsUpdated -= CurrentSession_CheckedLocationsUpdated;
            }
        }

        // Connect to Duckstation
        GameClient? gameClient = null;
        try
        {
            gameClient = new GameClient("duckstation");
        }
        catch (ArgumentException ex)
        {
            Log.Logger.Warning("Duckstation not running, open Duckstation and launch the game before connecting!");
            Context.ConnectButtonEnabled = true;
            return;
        }
        try
        {
            bool connected = gameClient.Connect();
            if (!connected)
            {
                Log.Logger.Warning("Duckstation not running, open Duckstation and launch the game before connecting!");
                Context.ConnectButtonEnabled = true;
                return;
            }
        }
        catch (ArgumentException ex)
        {
            Log.Logger.Warning("Duckstation not running, open Duckstation and launch the game before connecting!");
            Context.ConnectButtonEnabled = true;
            return;
        }

        Memory.GlobalOffset = Memory.GetDuckstationOffset();

        // Initialize ArchipelagoClient
        APClient = new ArchipelagoClient(gameClient);
        APClient.Connected += Client_Connected;
        APClient.Disconnected += Client_Disconnected;
        APClient.MessageReceived += Client_MessageReceived;

        // Connect to host and log in to slot => init Options, ItemManager, LocationManager
        await APClient.Connect(e.Host ?? "localhost:38281", "Mega Man Legends");
        if (!APClient.IsConnected)
        {
            Log.Logger.Error("Your host seems to be invalid.  Please confirm that you have entered it correctly.");
            Context.ConnectButtonEnabled = true;
            return;
        }
        PlayerName = e.Slot;
        await APClient.Login(PlayerName, !string.IsNullOrWhiteSpace(e.Password) ? e.Password : null);
        if (!APClient.IsLoggedIn)
        {
            Log.Logger.Error("Failed to login.  Please check your host, name, and password.");
            Context.ConnectButtonEnabled = true;
            return;
        }

        // Subscribe to item and location events
        APClient.ItemManager.ItemReceived += ItemManager_ItemReceived;
        APClient.LocationManager.EnableLocationsCondition = LocationManager_EnableLocationsCondition;
        APClient.LocationManager.LocationCompleted += LocationManager_LocationCompleted;
        APClient.CurrentSession.Locations.CheckedLocationsUpdated += CurrentSession_CheckedLocationsUpdated;

        // TODO: parse options
        GameLocations = LocationHelpers.BuildLocationList(APClient.Options);
        GameLocations = GameLocations.Where(x => x != null && !APClient.CurrentSession.Locations.AllLocationsChecked.Contains(x.Id)).ToList();

        int slot = APClient.CurrentSession.ConnectionInfo.Slot;

        // Scout location item data for future use
        long[] locationIds = GameLocations.Select(loc => (long)loc.Id).ToArray();
        ArchipelagoSession session = APClient.CurrentSession;
        Dictionary<long, ScoutedItemInfo> scoutedLocations = await session.Locations.ScoutLocationsAsync(locationIds);
        ScoutedLocationItemData = scoutedLocations.Keys.ToDictionary(
            locationId => locationId, locationId => scoutedLocations[locationId].Player.Slot == slot ? ItemDataDict[scoutedLocations[locationId].ItemId] : ItemDataDict[0]
        );

        // Check apworld version compatibility with host and log results
        Dictionary<string, object> slotData = await APClient.CurrentSession.DataStorage.GetSlotDataAsync(slot);
        if (slotData.TryGetValue("apworldVersion", out var versionValue) && versionValue != null)
        {
            if (SupportedVersions.Contains(versionValue.ToString().ToLower()))
            {
                Log.Logger.Information($"The host's AP world version is {versionValue} and the client version is {Version}.");
                Log.Logger.Information("These versions are known to be compatible.");
            }
            else
            {
                Log.Logger.Warning($"The host's AP world version is {versionValue} but the client version is {Version}.");
                Log.Logger.Warning("Please ensure these are compatible before proceeding.");
            }
        }
        else
        {
            Log.Logger.Error("Unable to retrieve apworldversion from slot data.");
        }
        Log.Logger.Information("Warnings and errors above are okay if this is your first time connecting to this multiworld server.");

        APClient.MonitorLocationsAsync(GameLocations);
        await APClient.ReceiveReady();

        Context.ConnectButtonEnabled = true;
        return;
    }

    private static async void Client_Connected(object? sender, EventArgs args)
    {
        if (APClient != null)
        {
            // Ensure player is in game before starting gameplay loop
            StartMMLTimer = new Timer();
            StartMMLTimer.Elapsed += new ElapsedEventHandler(StartMMLGame);
            StartMMLTimer.Interval = 5000;
            StartMMLTimer.Enabled = true;

            // Start gameplay loop
            GameLoopTimer = new Timer();
            GameLoopTimer.Elapsed += new ElapsedEventHandler(ModifyGameLoop);
            GameLoopTimer.Interval = 500;
            GameLoopTimer.Enabled = true;

            Log.Logger.Information("Connected to Archipelago");
            Log.Logger.Information($"Playing {APClient.CurrentSession.ConnectionInfo.Game} as {APClient.CurrentSession.Players.GetPlayerName(APClient.CurrentSession.ConnectionInfo.Slot)}");
        }
        return;
    }
    
    private static async void Client_Disconnected(object? sender, EventArgs args)
    {
        Log.Logger.Information("Disconnected from Archipelago");
        // Avoid ongoing timers affecting a new game.
        StartMMLTimer?.Enabled = false;
        GameLoopTimer?.Enabled = false;
        HasSubmittedGoal = false;
        return;
    }

    private static async void StartMMLGame(object? sender, ElapsedEventArgs e)
    {
        if (
            APClient != null &&
            APClient.ItemManager != null &&
            APClient.CurrentSession != null && 
            LocationManager_EnableLocationsCondition()
        )
        {
            StartMMLTimer?.Enabled = false;
            _ = APClient.ReceiveReady();
        }
        return;
    }

    private static async void ModifyGameLoop(object? sender, ElapsedEventArgs e)
    {
        if (
            APClient != null &&
            APClient.ItemManager != null &&
            APClient.CurrentSession != null
        )
        {
            try
            {
                if (LocationManager_EnableLocationsCondition())
                {
                    // Check goal
                    CheckGoalCondition();

                    // Do things when changing rooms
                    ushort currentLevelID = Memory.ReadUShort(Addresses.CurrentLevel.Address, Enums.Endianness.Big);
                    if (
                        (currentLevelID != PreviousLevelID) && 
                        MemoryHelpers.IsInGameOrCutscene() &&
                        !Memory.ReadBit(Addresses.SaveDataMenuFlag.Address, Addresses.SaveDataMenuFlag.BitNumber??0)
                    )
                    {
                        IsManagingLevelChange = true;
                    }
                    PreviousLevelID = currentLevelID;

                    if(
                        IsManagingLevelChange &&
                        MemoryHelpers.IsInGameOrCutscene() &&
                        !Memory.ReadBit(Addresses.SaveDataMenuFlag.Address, Addresses.SaveDataMenuFlag.BitNumber ?? 0) &&
                        !Memory.ReadBit(Addresses.LoadingFlag.Address, Addresses.LoadingFlag.BitNumber ?? 0) &&
                        !Memory.ReadBit(Addresses.ScreenWipeFlag.Address, Addresses.ScreenWipeFlag.BitNumber ?? 0) &&
                        LevelDataDict.TryGetValue(currentLevelID, out LevelData? currentLevelData)
                    )
                    {
                        System.Threading.Thread.Sleep(50);
                        switch (currentLevelData)
                        {
                            case { RoomName: "Ira's Room" }:
                                // Handle "Cure Ira's illness" location
                                if (
                                    ScoutedLocationItemData != null &&
                                    ScoutedLocationItemData.TryGetValue(111, out var iraScoutedItemData) &&
                                    LocationDataDict.TryGetValue(111, out var iraLocationData) &&
                                    iraLocationData.Name == "Cure Ira's illness" &&
                                    iraLocationData.TextBoxStartAddress != null
                                )
                                {
                                    OverwrittenTextData = TextHelpers.OverwriteText(iraLocationData.TextBoxStartAddress ?? 0, TextHelpers.EncodeYouGotItemWindow(iraScoutedItemData));
                                }
                                break;
                            case { RoomName: "Junk Shop" }:
                                //Handle "Rescue the shop owner's husband" location
                                if (
                                    ScoutedLocationItemData != null &&
                                    ScoutedLocationItemData.TryGetValue(104, out var rescueScoutedItemData) &&
                                    LocationDataDict.TryGetValue(104, out var rescueLocationData) &&
                                    rescueLocationData.Name == "Rescue the shop owner's husband" &&
                                    rescueLocationData.TextBoxStartAddress != null
                                )
                                {
                                    //OverwrittenTextData = TextHelpers.OverwriteText(rescueLocationData.TextBoxStartAddress ?? 0, TextHelpers.EncodeYouGotItemWindow(rescueScoutedItemData));
                                    Memory.WriteByteArray(rescueLocationData.TextBoxStartAddress ?? 0, TextHelpers.EncodeYouGotItemWindow(rescueScoutedItemData, [0x9F, 0x99, 0x00, 0xBD, 0xA9, 0x84]));
                                }
                                break;
                            case { RoomName: "City Hall Outdoors" }:
                                // Handle construction worker dialogue for Pick
                                List<byte[]> substrs =
                                    [
                                        TextHelpers.EncodeSimpleString("Huh? A pick?"),
                                        TextHelpers.newPage,
                                        TextHelpers.EncodeSimpleString("Never heard of it.\n:)"),
                                        TextHelpers.newPage,
                                        TextHelpers.EncodeSimpleString("Try looking elsewhere!"),
                                        TextHelpers.endWindow
                                    ];
                                byte[] workerTextChange = TextHelpers.ConcatArrayList(substrs);
                                Memory.WriteByteArray(Addresses.WorkerGetPickTextStart.Address, workerTextChange);

                                break;
                            default:
                                break;
                        }
                        IsManagingLevelChange = false;
                    }

                    // Write back any overwritten text
                    if (
                        !Memory.ReadBit(Addresses.TextBoxOpenFlag.Address, Addresses.TextBoxOpenFlag.BitNumber??7) && 
                        OverwrittenTextData != null
                    )
                    {
                        Memory.WriteByteArray(OverwrittenTextData.StartAddress, OverwrittenTextData.TextByteArr);
                        OverwrittenTextData = null;
                    }
                }

                // Handle receiving items after loading a save (leaving title screen)
                // Logic:
                // - Receive all non-zenny items after moving from title screen to in-game
                // - Open all containers/holes that were previously opened
                // Potential issues:
                // - Currently only works on bit checks, which is fine for now since we only have bit checks
                // - Only re-check container locations, so players can still get vanilla items from side-quest checks. But didn't want to risk breaking quest progression by writing to quest check addresses.
                bool isCurrentlyInTitleScreen = MemoryHelpers.IsInTitleScreen();
                if (
                    IsPreviouslyInTitleScreen && 
                    !isCurrentlyInTitleScreen
                )
                {
                    IsReceivingItemsAfterLoad = true;
                }
                IsPreviouslyInTitleScreen = isCurrentlyInTitleScreen;

                if (
                    IsReceivingItemsAfterLoad &&
                    LocationManager_EnableLocationsCondition()
                )
                {
                    IReadOnlyCollection<long> allLocationsChecked = APClient.CurrentSession.Locations.AllLocationsChecked;
                    if (allLocationsChecked.Count > 0)
                    {
                        Log.Logger.Information("Re-checking previously checked container locations...");
                        foreach (long locationID in allLocationsChecked)
                        {
                            if (LocationDataDict.TryGetValue((int)locationID, out LocationData? locationData))
                            {
                                if(new []{ LocationCategory.Container, LocationCategory.Hole, LocationCategory.Pickup }.Contains(locationData.Category))
                                {
                                    if (locationData.CheckAddressData.BitNumber != null)
                                    {
                                        Memory.WriteBit(locationData.CheckAddressData.Address, locationData.CheckAddressData.BitNumber ?? 0, true);
                                    }
                                    else
                                    {
                                        Log.Logger.Warning($"No check bit defined for location ID {locationID}. Please report this in the Discord thread!");
                                    }
                                }
                            }
                            else
                            {
                                Log.Logger.Warning($"Failed to receive item for location ID {locationID} after loading save. Please report this in the Discord thread!");
                            }
                        }
                    }
                    IReadOnlyCollection<ItemInfo> allItemsReceived = APClient.CurrentSession.Items.AllItemsReceived;
                    if (allItemsReceived.Count > 0)
                    {
                        Log.Logger.Information("Receiving non-zenny items from previously received items...");
                        foreach (ItemInfo itemInfo in allItemsReceived)
                        {
                            if(ItemDataDict.TryGetValue(itemInfo.ItemId, out ItemData? itemData))
                            {
                                if(itemData.Category != ItemCategory.Zenny)
                                {
                                    ItemHelpers.ReceiveGenericItem(itemData);
                                }
                            }
                            else
                            {
                                Log.Logger.Warning($"Failed to receive item ID {itemInfo.ItemId} after loading save. Please report this in the Discord thread!");
                            }
                        }
                    }
                    IsReceivingItemsAfterLoad = false;
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Warning("Encountered an error while managing the game loop.");
                Log.Logger.Warning(ex.ToString());
                Log.Logger.Warning("This is not necessarily a problem if it happens during release or collect.");
            }
        }
        return;
    }

    private static void CheckGoalCondition()
    {
        if (
            APClient != null && (
                !HasSubmittedGoal ||
                LocationManager_EnableLocationsCondition()
            )
        )
        {
            APClient.Options.TryGetValue("goal", out var goalValueObj);
            if (APClient != null && APClient.Options != null)
            {
                int goalValue = goalValueObj as int? ?? 0;
                bool isGoalComplete = (CompletionGoal)goalValue switch
                {
                    CompletionGoal.Juno => Memory.ReadBit(Addresses.GoalJunoFlag.Address, Addresses.GoalJunoFlag.BitNumber ?? 0),
                    _ => false
                };
                if (isGoalComplete)
                {
                    APClient.SendGoalCompletion();
                    HasSubmittedGoal = true;
                }
            }
        }
        return;
    }

    private static bool LocationManager_EnableLocationsCondition()
    {
        bool[] conditions = [
            MemoryHelpers.IsInGameOrCutscene(),
            !Memory.ReadBit(Addresses.ScreenWipeFlag.Address, Addresses.ScreenWipeFlag.BitNumber??0),
            !Memory.ReadBit(Addresses.LoadingFlag.Address, Addresses.LoadingFlag.BitNumber??0),
            //!Memory.ReadBit(Addresses.DungeonMapFlag.Address, Addresses.DungeonMapFlag.BitNumber??0),
            //!Memory.ReadBit(Addresses.PauseMenuFlag.Address, Addresses.PauseMenuFlag.BitNumber??0),
            !Memory.ReadBit(Addresses.SaveDataMenuFlag.Address, Addresses.SaveDataMenuFlag.BitNumber??0),
            !Memory.ReadBit(Addresses.CameraAlteredFlag.Address, Addresses.CameraAlteredFlag.BitNumber??0) || (
                Memory.ReadShort(Addresses.CurrentLevel.Address) == 0x0308 // Allow locations to be checked during the Beast Hunter minigame since the camera is altered but the player is still in game.
            )
        ];
        return conditions.All(value => value);
    }

    private void LocationManager_LocationCompleted(object? sender, LocationCompletedEventArgs e)
    {
        if (
            APClient != null &&
            APClient.LocationManager != null && 
            APClient.CurrentSession != null &&
            ScoutedLocationItemData != null
        )
        {
            // Use scouted location item to rewrite textbox
            LocationData locationData = LocationDataDict[e.CompletedLocation.Id];
            if (locationData.TextBoxStartAddress != null)
            {
                ItemData itemData = ScoutedLocationItemData[e.CompletedLocation.Id];
                OverwrittenTextData = TextHelpers.OverwriteText(locationData.TextBoxStartAddress ?? 0, TextHelpers.EncodeYouGotItemWindow(itemData));
            }
        }
        return;
    }

    private static void ItemManager_ItemReceived(object? sender, ItemReceivedEventArgs args)
    {
        if (
            APClient != null &&
            APClient.CurrentSession != null && 
            ItemDataDict.TryGetValue(args.Item.Id, out ItemData? itemData)
        )
        {
            ItemHelpers.ReceiveGenericItem(itemData);
            Log.Logger.Debug($"Item Received: {JsonConvert.SerializeObject(args.Item)}");
        }
        return;
    }

    private static async void Client_MessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        Log.Logger.Information(JsonConvert.SerializeObject(e.Message));
        return;
    }

    private static async void CurrentSession_CheckedLocationsUpdated(System.Collections.ObjectModel.ReadOnlyCollection<long> newCheckedLocations)
    {
        if (
            APClient != null &&
            APClient.ItemManager != null &&
            APClient.CurrentSession != null
        )
        {
            if (!LocationManager_EnableLocationsCondition())
            {
                Log.Logger.Error("Check sent while not in game. Please report this in the Discord thread!");
            }

        }
        return;
    }
}