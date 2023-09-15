using System;
using System.Collections.Generic;
using System.Linq;
using CrowdControl.Common;
using JetBrains.Annotations;
using ConnectorType = CrowdControl.Common.ConnectorType;

namespace CrowdControl.Games.Packs;

[UsedImplicitly]
public class SimCitySNES : SNESEffectPack
{
    public SimCitySNES(UserRecord player, Func<CrowdControlBlock, bool> responseHandler,
        Action<object> statusUpdateHandler) : base(player, responseHandler, statusUpdateHandler)
    {

        _bidwar_autobulldoze = new Dictionary<string, Func<bool>>
        {
            { "enabled", () => Connector.SetBits(ADDR_OPTIONS, 0x01, out _) },
            { "disabled", () => Connector.UnsetBits(ADDR_OPTIONS, 0x01, out _) }
        };
        _bidwar_autotax = new Dictionary<string, Func<bool>>
        {
            { "enabled", () => Connector.SetBits(ADDR_OPTIONS, 0x02, out _) },
            { "disabled", () => Connector.UnsetBits(ADDR_OPTIONS, 0x02, out _) }
        };
        _bidwar_autogoto = new Dictionary<string, Func<bool>>
        {
            { "enabled", () => Connector.SetBits(ADDR_OPTIONS, 0x04, out _) },
            { "disabled", () => Connector.UnsetBits(ADDR_OPTIONS, 0x04, out _) }
        };
    }

    private readonly Dictionary<string, Func<bool>> _bidwar_autobulldoze;
    private readonly Dictionary<string, Func<bool>> _bidwar_autotax;
    private readonly Dictionary<string, Func<bool>> _bidwar_autogoto;

    private const uint ADDR_GIFT1 = 0x7E03F5;
    private const uint ADDR_GIFT2 = 0x7E03F6;
    private const uint ADDR_GIFT3 = 0x7E03F7;
    private const uint ADDR_GIFT4 = 0x7E03F8;
    private const uint ADDR_YEAR = 0x7E0B53;
    private const uint ADDR_MONTH = 0x7E0B55;
    private const uint ADDR_DEMON_SEASON = 0x7E0B56; //poking to 01 makes the pallelt all weird for the next season, setting it back to 00 fixes
    private const uint ADDR_BUILD_ITEM = 0x7E020D;
    private const uint ADDR_BUILD_SELECTION_BASE = 0x7E029B;
    private const uint ADDR_BUILD_FORCE = 0x7E023C; //only works on bulldoze, roads, rails and parks
    private const uint ADDR_OPTIONS = 0x7E0195;
    private const uint ADDR_GAMESTATE = 0x7E1FF6; //poke 50 to gameover
    private const uint ADDR_GAMESPEED = 0x7E0193; //00 fastest - 03 stop
    private const uint ADDR_DISASTER = 0x7E0197;

    private const uint ADDR_CURSOR_X = 0x7E01EB;
    private const uint ADDR_CURSOR_Y = 0x7E01ED;


    private const uint ADDR_GAME_TYPE   = 0x7E003E;
    private const uint ADDR_SCENARIO    = 0x7E0040;
    private const uint ADDR_POPULATION  = 0x7E0BA5;
    private const uint ADDR_TIMER       = 0x7E0C0D;
    private const uint ADDR_UFO_TRIGGER = 0x7FF000;

    private static readonly (ushort min, ushort max) CURSOR_RANGE_X = (0x38, 0xE8);
    private static readonly (ushort min, ushort max) CURSOR_RANGE_Y = (0x30, 0xC0);

    //TAXES
    private const uint ADDR_TRANSIT_FUND = 0x7E0D79;
    private const uint ADDR_POLICE_FUND = 0x7E0D7B;
    private const uint ADDR_FIRE_FUND = 0x7E0D7D;
    //other tax stuff is weird... found like 3 places it might be...
    private const uint ADDR_MONEY = 0x7E0B9D; //max cash is 999,999. but unable to actually take or give that much

    private const uint ADDR_SCREENSHAKE = 0x7E0C0F; //2 bytes poking with value causes screen to shark for that long

    private const uint ADDR_OVERLAY_MESSAGE = 0x7E0389;  //small messages like "roads congested", doesn't interupt gameplay
    private const uint ADDR_OVERLAY_ACTIVE = 0x7E0387; //if this is FF then the message is up
    private const uint ADDR_HELPER_MESSAGE = 0x7E0395; //takes you to a scene with your helper, also gives gifts 
    private const uint ADDR_HELPER_ID = 0x7E0397; //this value determines the message we get above

    private volatile bool _demonSeason = false;
    private volatile bool _forcebulldoze = false;
    private volatile bool _shakescreen = false;

    private byte gametype;
    private byte scenario;
    private ushort timer;
    private ushort pop;
    private byte poph;

    private class GiftAssociation
    {
        public string GiftName { get; }
        public byte GiftID { get; }
        public byte MessageID { get; }

        public GiftAssociation(string giftName, byte giftID, byte messageID)
        {
            GiftName = giftName;
            GiftID = giftID;
            MessageID = messageID;
        }
    }

    private class DisasterAssociation
    {
        public string DisasterName { get; }
        public byte DisasterID { get; }
        public byte DisasterMessageID { get; }
        public uint DisasterAddress { get; }
        public int DisasterCheck { get; }

        public DisasterAssociation(string disasterName, byte disasterID, byte disasterMessageID, uint disasterAddress, int disasterCheck)
        {
            DisasterName = disasterName;
            DisasterID = disasterID;
            DisasterMessageID = disasterMessageID;
            DisasterAddress = disasterAddress;
            DisasterCheck = disasterCheck;
        }
    }

    private class BuildingAssociation
    {
        public string BuildingName { get; }
        public byte BuildingID { get; }
        public uint BuildingUIAddress { get; }
        public uint Cost { get; }

        public BuildingAssociation(string buildingName, byte buildingID, uint buildingUI, uint cost)
        {
            BuildingName = buildingName;
            BuildingID = buildingID;
            BuildingUIAddress = buildingUI;
            Cost = cost;
        }
    }

    private class MessageAssociation
    {
        public string MessageName { get; }
        public byte MessageID { get; }

        public MessageAssociation(string messageName, byte messageID)
        {
            MessageName = messageName;
            MessageID = messageID;
        }
    }

    private readonly Dictionary<string, GiftAssociation> _game_gifts = new(StringComparer.InvariantCultureIgnoreCase)
    {
        //SafeName, MessageName, giftID, messageID
        {"mayorhouse", new GiftAssociation("Mayor House", 0x01, 0x2C) },
        {"bank", new GiftAssociation("Bank", 0x02, 0x0B) },
        {"park", new GiftAssociation("Park", 0x03, 0x0D) }, //this messageID lets them pick between park or casino
        {"casino", new GiftAssociation("Casino", 0x05, 0x0D) }, //but we know the ID so we could still force the choice
        {"zoo", new GiftAssociation("Zoo", 0x04, 0x0E) },
        {"landfill", new GiftAssociation("Land Fill", 0x06, 0x10) },
        {"policehq", new GiftAssociation("Police HQ", 0x07, 0x11) },
        {"firehq", new GiftAssociation("Fire Department HQ", 0x08, 0x12) },
        {"fountain", new GiftAssociation("Fountain", 0x09, 0x13) },
        {"mariostatue", new GiftAssociation("Mario Statue", 0x0A, 0x14) },
        {"expo", new GiftAssociation("Expo", 0x0B, 0x16) },
        {"windmill", new GiftAssociation("Windmill", 0x0C, 0x17) },
        {"library", new GiftAssociation("Library", 0x0D, 0x18) },
        {"largepark", new GiftAssociation("Large Park", 0x0E, 0x19) },
        {"station", new GiftAssociation("Train Station", 0x0F, 0x1A) }
    };

    private readonly Dictionary<string, DisasterAssociation> _game_disasters = new(StringComparer.InvariantCultureIgnoreCase)
    {
        //SafeName, MessageName, disasterID, disasterMessageID, disasterAddress, disasterCheck
        //disasterAddress tells us if that disaster is active or possible
        //bowser, and torando need their disasterCheck value to be 0
        //but things like planecrash needs it to be 1 as it will cause that plane to then crash

        {"bowser", new DisasterAssociation("Bowser", 0x20, 0x09, 0x7E0A91, 0) },
        {"earthquake", new DisasterAssociation("Earthquake", 0x10, 0x0A, 0x7E0A80, 0) },
        {"fire", new DisasterAssociation("Fire", 0x01, 0x20, 0x7E0A80, 0) },
        {"flood", new DisasterAssociation("Flood", 0x02, 0x21, 0x7E0A80, 0) },
        {"planecrash", new DisasterAssociation("Plane Crash", 0x04, 0x22, 0x7E0A8D, 1) },
        {"tornado", new DisasterAssociation("Tornado", 0x08, 0x23, 0x7E0A8B, 0) },
        {"ufo", new DisasterAssociation("UFO Attack", 0xFF, 0x30, 0x7E0A80, 0) },
        //Only the above I know how to trigger correctly, sadly setting the other unused flags do nothing
        //the below can be trigger sometimes by poking some additional bits but seem in-consistent 
        {"nuclear", new DisasterAssociation("Nuclear Explosion", 0x00, 0x24, 0x7E0A80, 0) },
        {"shipwreck", new DisasterAssociation("Shipwreck", 0x00, 0x2B, 0x7E0A96, 0) }, //this one spawns a ship in the bottom right and causes it to wreck if the map has no water
        {"disaster", new DisasterAssociation("Unknown Disaster", 0x00, 0x3D, 0x7E0A80, 0) } //This one just gives the warning. 
        //, 0x7E0A80 is a place holder to just let it trigger
    };

    private readonly Dictionary<string, BuildingAssociation> _game_building = new(StringComparer.InvariantCultureIgnoreCase)
    {
        //SafeName, BuildingName, BuildingID, BuildingUI, Cost
        {"bulldoze", new BuildingAssociation("Bulldoze", 0x00, 0x7E029B, 1) },
        {"roads", new BuildingAssociation("Roads", 0x01, 0x7E029C, 10) },
        {"rails", new BuildingAssociation("Rails", 0x02, 0x7E029D, 20) },
        {"power", new BuildingAssociation("Powerlines", 0x03, 0x7E029E, 5) },
        {"park", new BuildingAssociation("Park", 0x04, 0x7E029F, 10) },
        {"residental", new BuildingAssociation("Residental", 0x05, 0x7E02A0, 100) },
        {"commercial", new BuildingAssociation("Commercial", 0x06, 0x7E02A1, 100) },
        {"industrial", new BuildingAssociation("Industrial", 0x07, 0x7E02A2, 100) },
        {"policedept", new BuildingAssociation("Police Department", 0x08, 0x7E02A3, 500) },
        {"firedept", new BuildingAssociation("Fire Station", 0x09, 0x7E02A4, 500) },
        {"stadium", new BuildingAssociation("Stadium", 0x0A, 0x7E02A5, 3000) },
        {"seaport", new BuildingAssociation("Sea Port", 0x0B, 0x7E02A6, 5000) },
        {"coal", new BuildingAssociation("Coal Power", 0x0E, 0x7E02A7, 3000) },
        {"nuclear", new BuildingAssociation("Nuclear Power", 0x0D, 0x7E02A8, 5000) },
        {"airport", new BuildingAssociation("Airport", 0x0C, 0x7E02A9, 10000) }
    };

    private readonly Dictionary<string, MessageAssociation> _game_messages = new(StringComparer.InvariantCultureIgnoreCase)
    {
        //SafeName, MessageName, MessageID
        {"msgresidental", new MessageAssociation("More Residental Zones Needed.", 0x01) },
        {"msgcommercial", new MessageAssociation("More Commerical Zones Needed.", 0x02) },
        {"msgindustrial", new MessageAssociation("More Industrial Zones Needed.", 0x03) },
        {"msgroads", new MessageAssociation("More roads required.", 0x04) },
        {"msgrail", new MessageAssociation("Inadequate Rail System.", 0x05) },
        {"msgpowerplant", new MessageAssociation("Build a Power Plant.", 0x06) },
        {"msgstadium", new MessageAssociation("Residents demand a Stadium.", 0x07) },
        {"msgseaport", new MessageAssociation("Industry requires a Seas Port.", 0x08) },
        {"msgairport", new MessageAssociation("Commerce requires an Airport.", 0x09) },
        {"msgpollution", new MessageAssociation("Pollution very high.", 0x0A) },
        {"msgcrime", new MessageAssociation("Crime very high.", 0x0B) },
        {"msgtraffic", new MessageAssociation("Frequent traffic james reported.", 0x0C) },
        {"msgfiredept", new MessageAssociation("Citizens demand a Fire Department.", 0x0D) },
        {"msgpolicedept", new MessageAssociation("Citizens demand a Police Department.", 0x0E) },
        {"msgblackouts", new MessageAssociation("Blackouts reported. Check power map.", 0x0F) },
        {"msgtax", new MessageAssociation("Citizens upset. The tax rate is too high.", 0x10) },
        {"msgdeteriorating", new MessageAssociation("Roads deteriorating, due to lack of funds.", 0x11) },
        {"msgfirefund", new MessageAssociation("Fire departments need funding.", 0x12) },
        {"msgpolicefund", new MessageAssociation("Police departments need funding.", 0x13) },
        {"msgshipwreck", new MessageAssociation("Shipwreck reported!", 0x14) },
        {"msg5years", new MessageAssociation("5 years to complete scenario.", 0x15) },
        {"msg4years", new MessageAssociation("4 years to complete scenario.", 0x16) },
        {"msg3years", new MessageAssociation("3 years to complete scenario.", 0x17) },
        {"msgexplosion", new MessageAssociation("Explosion detected!", 0x18) },
        {"msg2years", new MessageAssociation("2 years to complete scenario.", 0x19) },
        {"msg1year", new MessageAssociation("1 year to complete scenario.", 0x1A) },
        {"msgbrownouts", new MessageAssociation("Brownouts, build another Power Plant.", 0x1B) },
        {"msgheavytraffic", new MessageAssociation("Heavy Traffic reported.", 0x1C) },
        //{"msgblank", new MessageAssociation("", 0x1D) },
        {"msgunabletosave", new MessageAssociation("unable to save.", 0x1E) },
        {"msgsaved", new MessageAssociation("Save completed.", 0x1F) },
        {"msgonemoment", new MessageAssociation("One moment please...", 0x20) },
        {"msggoodbye", new MessageAssociation("See you soon. Good bye!", 0x21) }
    };


    public override EffectList Effects
    {
        get
        {
            ParameterDef enableDisable = new("Enabled / Disabled", "enableDisable",
                new Parameter("Enabled", "enabled"),
                new Parameter("Disabled", "disabled"));

            List<Effect> effects = new List<Effect>
            {
                //give and take sorta work. would like to give the player * 100 what the input is
                //sometimes it doesnt take, not sure if its tripping up somewhere
                new("Give Money", "givemoney") { Quantity = 9999 },
                new("Take Money", "takemoney") { Quantity = 9999 },
                //new("Choose a Disaster ", "disaster", ItemKind.Folder),
                //new("Give gift of ", "present", ItemKind.Folder),
                //new("Switch building item to ", "building", ItemKind.Folder),
                new("Send a helpful message ", "helpfulmessage")
                {
                    Parameters = new ParameterDef("Message", "message",
                        _game_messages.Take(33).Select(t => new Parameter($"{t.Value.MessageName}", t.Key))
                    )
                },
                new("Increase Transport Funds", "increasetransport") { Quantity = 99 },
                new("Decrease Transport Funds", "decreasetransport") { Quantity = 99 },
                new("Increase Police Funds", "increasepolice") { Quantity = 99 },
                new("Decrease Police Funds", "decreasepolice") { Quantity = 99 },
                new("Increase Fire Funds", "increasefire") { Quantity = 99 },
                new("Decrease Fire Funds", "decreasefire") { Quantity = 99 },
                new("Enable Demon Season", "demonseason") { Duration = 60, IsDurationEditable = false },
                new("Change Month", "changemonth")
                {
                    Quantity = 99
                }, //would be nice if this looped back? so if it was on 11 and i added 11, it would go to 10.
                new("Change Year", "changeyear") { Quantity = 99 },
                new("Game Over", "gameover"),
                new("Force Bulldoze", "forcebulldoze") { Duration = 15 },
                new("Max Speed", "maxspeed"),
                new("Medium Speed", "mediumspeed"),
                new("Low Speed", "lowspeed"),
                new("Stop Time", "stoptime"),

                /*new("Enable Auto-Bulldoze", "enableautobulldoze"),
                new("Disable Auto-Bulldoze", "disableautobulldoze"),
                new("Enable Auto-Tax", "enableautotax"),
                new("Disable Auto-Tax", "disableautotax"),
                new("Enable Auto-Goto", "enableautogoto"),
                new("Disable Auto-Goto", "disableautogoto"),*/

                new("Auto-Bulldoze", "autobulldoze", ItemKind.BidWar) { Parameters = enableDisable },
                new("Auto-Tax", "autotax", ItemKind.BidWar) { Parameters = enableDisable },
                new("Auto-Goto", "autogoto", ItemKind.BidWar) { Parameters = enableDisable },

                new("Shake the screen!", "shakescreen") { Quantity = 9 }
            };

            effects.AddRange(_game_gifts.Take(15).Select(t =>
                new Effect($"Give {t.Value.GiftName}", $"present_{t.Key}") { Category = "Give Item" }));
            effects.AddRange(_game_disasters.Take(7).Select(t =>
                new Effect($"Send {t.Value.DisasterName}", $"disaster_{t.Key}") { Category = "Send Disasters" }));
            effects.AddRange(_game_building.Take(15).Select(t =>
                new Effect($"Set Building to {t.Value.BuildingName}", $"building_{t.Key}") { Category = "Set Active Building" }));
            //effects.AddRange(_game_messages.Take(33).Select(t => new Effect($"{t.Value.MessageName}", t.Key, ItemKind.Usable, "simCitySNESHelpfulMessage")));

            return effects;
        }
    }

    public override ROMTable ROMTable => new[]
    {
        new ROMInfo("SimCity (v1.0) (U) (Headered)", "SimCitySNES.bps",
            (stream, bytes) =>
            {
                var deheadered = Patching.Truncate(stream, 0x200, 0x80000);
                return deheadered.success ? Patching.BPS(stream, bytes) : deheadered;
            }, ROMStatus.ValidUnpatched, s => Patching.MD5(s, "ee177068d94ede4c95ec540b0db255db")),
        new ROMInfo("SimCity (v1.0) (U) (Unheadered)", "SimCitySNES.bps", Patching.BPS, ROMStatus.ValidUnpatched, s => Patching.MD5(s, "23715fc7ef700b3999384d5be20f4db5")),
        new ROMInfo("SimCity - Crowd Control", null, Patching.Ignore, ROMStatus.ValidPatched, s => Patching.MD5(s, "d1077c8e9e8926cdb540f364925aaa9f"))
    };

    public override List<(string, Action)> MenuActions => new();

    public override Game Game { get; } = new("Sim City", "SimCitySNES", "SNES", ConnectorType.SNESConnector);

    protected override bool IsReady(EffectRequest request) => Connector.Read8(ADDR_GAMESTATE, out byte b) && (b == 0x00) && Connector.Read8(ADDR_GAME_TYPE, out byte a) && (a != 0x00);

    protected override void RequestData(DataRequest request) => Respond(request, request.Key, null, false, $"Variable name \"{request.Key}\" not known");

    protected override void StartEffect(EffectRequest request)
    {
        if (!IsReady(request))
        {
            DelayEffect(request);
            return;
        }

        sbyte sign = 1;
        string[] codeParams = FinalCode(request).Split('_');
        switch (codeParams[0])
        {
            case "demonseason":
            {
#pragma warning disable CS0612
                StartTimed(request,
                    () => (!_demonSeason && Connector.Read8(ADDR_GAMESTATE, out byte b) && (b == 0x00) && Connector.Read8(ADDR_GAMESPEED, out byte c) && (c != 03)),
                    () =>
                    {
                        bool result = Connector.Write8(ADDR_DEMON_SEASON, 0x01);
                        if (result)
                        {
                            _demonSeason = true;
                            Connector.SendMessage($"{request.DisplayViewer} enabled the demon season.");
                        }
                        return result;
                    },
                    TimeSpan.FromSeconds(60));
#pragma warning restore CS0612
                return;
            }
            case "forcebulldoze":
            {
                RepeatAction(request,
                    () => (!_forcebulldoze) && Connector.IsZero8(ADDR_GAMESTATE),
                    () => {
                        LockUI();
                        _forcebulldoze = true;
                        return Connector.SendMessage($"{request.DisplayViewer} has enabled the forced bulldoze.");
                    }, TimeSpan.FromSeconds(2.5),
                    () => Connector.IsZero8(ADDR_GAMESTATE), TimeSpan.FromSeconds(1),
                    () =>
                    {
                        var (x, y) = GetRandomLocation();
                        return Connector.Write8(ADDR_CURSOR_X, x) &&
                               Connector.Write8(ADDR_CURSOR_Y, y) &&
                               Connector.Write8(ADDR_BUILD_ITEM, 0x00) &&
                               Connector.Write8(ADDR_BUILD_FORCE, 0x01);
                    },
                    TimeSpan.FromSeconds(0.2), true).WhenCompleted.Then(_ =>
                {
                    Connector.SendMessage($"{request.DisplayViewer}'s forced bulldoze is over!");
                    ClearUI();
                    _forcebulldoze = false;
                });
                return;
            }
            case "shakescreen":
            {
                uint seconds = request.Quantity;
#pragma warning disable CS0612
                RepeatAction(request, TimeSpan.FromSeconds(seconds),
                    () => (!_shakescreen) && Connector.IsZero8(ADDR_GAMESTATE) && Connector.IsZero8(ADDR_SCREENSHAKE),
                    () => {
                        _shakescreen = true;
                        return Connector.SendMessage($"{request.DisplayViewer} shook your screen for {seconds} seconds!.");
                    }, TimeSpan.FromSeconds(2.5),
                    () => Connector.IsZero8(ADDR_GAMESTATE), TimeSpan.FromSeconds(1),
                    () => Connector.Write8(ADDR_SCREENSHAKE, 0x7F),
                    TimeSpan.FromSeconds(0.5), true).WhenCompleted.Then(_ => {
                    Connector.SendMessage($"{request.DisplayViewer}'s screenshake is over!");
                    _shakescreen = false;
                });
#pragma warning restore CS0612
                return;
            }
            case "gameover":
            {
                TryEffect(request,
                    () => Connector.Write8(ADDR_GAMESTATE, 0x50),
                    () => true,
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} decided you had enough.");
                    });
                return;
            }
            case "autobulldoze":
            {
                BidWar(request, _bidwar_autobulldoze);
                return;
            }
            case "autotax":
            {
                BidWar(request, _bidwar_autotax);
                return;
            }
            case "autogoto":
            {
                BidWar(request, _bidwar_autogoto);
                return;
            }
            case "maxspeed":
            {
                TryEffect(request,
                    () => Connector.Read8(ADDR_GAMESPEED, out byte b) && (b != 0),
                    () => Connector.Write8(ADDR_GAMESPEED, 0x00),
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} set the max speed!");
                    });
                return;
            }
            case "mediumspeed":
            {
                TryEffect(request,
                    () => Connector.Read8(ADDR_GAMESPEED, out byte b) && (b != 1),
                    () => Connector.Write8(ADDR_GAMESPEED, 0x01),
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} set the medium speed!");
                    });
                return;
            }
            case "lowspeed":
            {
                TryEffect(request,
                    () => Connector.Read8(ADDR_GAMESPEED, out byte b) && (b != 2),
                    () => Connector.Write8(ADDR_GAMESPEED, 0x02),
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} set the low speed!");
                    });
                return;
            }
            case "stoptime":
            {
                TryEffect(request,
                    () => Connector.Read8(ADDR_GAMESPEED, out byte b) && (b != 3),
                    () => Connector.Write8(ADDR_GAMESPEED, 0x03),
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} stopped time!");
                    });
                return;
            }
            case "takemoney":
                sign = -1;
                goto case "givemoney";
            case "givemoney":
            {
                uint money = request.Quantity;
                long newTotal = 0;
                TryEffect(request,
                    () =>
                    {
                        if (!Connector.Read24LE(ADDR_MONEY, out uint value)) { return false; }
                        newTotal = value + (money * sign);
                        return CheckRange(newTotal, out _, 0, 999999, false);
                    },
                    () => Connector.Write24LE(ADDR_MONEY, (uint)newTotal),
                    () => { Connector.SendMessage($"{request.DisplayViewer} sent you {money} money."); });
                return;
            }
            case "increasetransport":
            {
                uint tax = request.Quantity;
                TryEffect(request,
                    () => Connector.RangeAdd8(ADDR_TRANSIT_FUND, tax, 0, 100, false),
                    () => true,
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} increased the Transportation Funds by {tax}%");
                    });
                return;
            }
            case "decreasetransport":
            {
                uint tax = request.Quantity;
                TryEffect(request,
                    () => Connector.RangeAdd8(ADDR_TRANSIT_FUND, -tax, 0, 100, false),
                    () => true,
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} decreased the Transportation Funds by {tax}%");
                    });
                return;
            }
            case "increasepolice":
            {
                uint tax = request.Quantity;
                TryEffect(request,
                    () => Connector.RangeAdd8(ADDR_POLICE_FUND, tax, 0, 100, false),
                    () => true,
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} increased the Police Funds by {tax}%");
                    });
                return;
            }
            case "decreasepolice":
            {
                uint tax = request.Quantity;
                TryEffect(request,
                    () => Connector.RangeAdd8(ADDR_POLICE_FUND, -tax, 0, 100, false),
                    () => true,
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} decreased the Police Funds by {tax}%");
                    });
                return;
            }
            case "increasefire":
            {
                uint tax = request.Quantity;
                TryEffect(request,
                    () => Connector.RangeAdd8(ADDR_FIRE_FUND, tax, 0, 100, false),
                    () => true,
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} increased the Fire Funds by {tax}%");
                    });
                return;
            }
            case "decreasefire":
            {
                uint tax = request.Quantity;
                TryEffect(request,
                    () => Connector.RangeAdd8(ADDR_FIRE_FUND, -tax, 0, 100, false),
                    () => true,
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} decreased the Fire Funds by {tax}%");
                    });
                return;
            }
            case "changemonth":
            {
                uint toAdd = request.Quantity;
                byte cMonth = 0;
                TryEffect(request,
                    () => Connector.Read8(ADDR_MONTH, out cMonth),
                    () =>
                    {
                        cMonth += (byte)(toAdd - 1u); //subtract to get to 0-indexed
                        uint years = cMonth / 12u;
                        return Connector.Write8(ADDR_MONTH, (byte)((cMonth % 12u) + 1)) &&
                               ((years == 0) || Connector.RangeAdd16(ADDR_YEAR, years, 1, 9999, false));
                    },
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} sent the city though time and space!");
                    });
                return;
            }
            case "changeyear":
            {
                uint year = request.Quantity;
                TryEffect(request,
                    () => Connector.RangeAdd16(ADDR_YEAR, year, 1, 9999, false),
                    () => true,
                    () =>
                    {
                        Connector.SendMessage($"{request.DisplayViewer} sent the city though time and space!");
                    });
                return;
            }
            case "present":
                var pType = _game_gifts[codeParams[1]];
                GivePresent(request, pType.MessageID, pType.GiftName);
                return;
            case "disaster":
                var dType = _game_disasters[codeParams[1]];

                if (dType.DisasterID == 0xFF)
                {
#pragma warning disable CS0612
                    StartTimed(request,
                        () => (Connector.Read8(0x7E0AF0, out byte a) && (a == 0x00) && Connector.Read8(ADDR_GAMESTATE, out byte b) && (b == 0x00)),
                        () =>
                        {


                            if (!Connector.Read8(ADDR_GAME_TYPE, out gametype)) return false;
                            if (!Connector.Read8(ADDR_SCENARIO, out scenario)) return false;
                            //if (!Connector.Read16(ADDR_POPULATION, out pop)) return false;
                            //if (!Connector.Read8(ADDR_POPULATION + 2, out poph)) return false;
                            if (!Connector.Read16(ADDR_TIMER, out timer)) return false;


                            if (!Connector.Write8(ADDR_GAME_TYPE, 0x03)) return false;
                            if (!Connector.Write8(ADDR_SCENARIO,  0x06)) return false;
                            //if (!Connector.Freeze16(ADDR_POPULATION, 0x4C08)) return false;
                            //if (!Connector.Freeze8(ADDR_POPULATION+2, 0x02)) return false;
                            if (!Connector.Freeze16(ADDR_TIMER, 0x0130)) return false;
                            //if (!Connector.Freeze8(ADDR_OVERLAY_ACTIVE, 0xFF)) return false;
                            //if (!Connector.Freeze8(ADDR_OVERLAY_MESSAGE, 0)) return false;
                            if (!Connector.Freeze8(0x7E0AB7, 0x01)) return false;

                            if (!Connector.Write8(ADDR_UFO_TRIGGER, 0xAB)) return false;
                                

                            Connector.SendMessage($"{request.DisplayViewer} has sent a UFO attack!");

                            return true;
                        },
                        TimeSpan.FromSeconds(10));
#pragma warning restore CS0612
                    return;
                }
                SendDisaster(request, dType.DisasterID, dType.DisasterAddress, dType.DisasterCheck, dType.DisasterName);
                return;
            case "building":
                var bType = _game_building[codeParams[1]];
                SetBuilding(request, bType.BuildingID, bType.BuildingUIAddress, bType.BuildingName, bType.Cost);
                return;
            case "helpfulmessage":
                var mType = _game_messages[codeParams[1]];
                SendHelpfulMessage(request, mType.MessageID, mType.MessageName);
                return;
        }
    }

    private (byte x, byte y) GetRandomLocation()
        => ((byte) RNG.Next(CURSOR_RANGE_X.min, CURSOR_RANGE_X.max),
            (byte) RNG.Next(CURSOR_RANGE_Y.min, CURSOR_RANGE_Y.max));

    private void GivePresent(EffectRequest request, byte pType, string giftName)
    {
        TryEffect(request,
            () => Connector.Write8(ADDR_HELPER_ID, pType),
            () => Connector.Write8(ADDR_HELPER_MESSAGE, 0x01),
            () =>
            {
                Connector.SendMessage($"{request.DisplayViewer} sent you a {giftName}.");
            });
    }

    private void SendDisaster(EffectRequest request, byte dType, ulong disasterAddress, int diasterCheck, string disasterName)
    {
        TryEffect(request,
            () => Connector.Read8(disasterAddress, out byte b) && (b == diasterCheck),
            () => Connector.Write8(ADDR_DISASTER, dType),
            () =>
            {
                Connector.SendMessage($"{request.DisplayViewer} sent a {disasterName} your way!");
            });
    }

    private void SetBuilding(EffectRequest request, byte bType, ulong BuildingUIAddress, string buildingName, uint cost)
    {
        TryEffect(request,
            () => Connector.Read32LE(ADDR_MONEY, out uint money) && (money >= cost) 
                                                                 && Connector.IsNotEqual8(ADDR_BUILD_ITEM, bType),
            () => Connector.Write8(ADDR_BUILD_ITEM, bType),
            () =>
            {
                ClearUI(); //I know we can do this better, we have the current active ID with the first Read above.
                //I just didn't know how to get that variable outside of spot to then write 00 to the ADDR_BUILD_SELECTION_BASE + bType
                Connector.Write8(BuildingUIAddress, 0x01);
                Connector.SendMessage($"{request.DisplayViewer} forced you to build only {buildingName}!");
            });
    }

    private void SendHelpfulMessage(EffectRequest request, byte mType, string messageName)
    {
        // Log.Message(mType);
        // Log.Message(messageName);
        TryEffect(request,
            () => Connector.Read8(ADDR_OVERLAY_ACTIVE, out byte b) && (b == 0),
            () => Connector.Write8(ADDR_OVERLAY_MESSAGE, mType),
            () =>
            {
                Connector.SendMessage($"{request.DisplayViewer} sent you a helpful message!.");
            });
    }

    private void ClearUI() => Connector.Write(0x7E029B, new byte[0x7E02AB - 0x7E029B]);
    /*{
        Connector.Write8(0x7E029B, 0x00);
        Connector.Write8(0x7E029C, 0x00);
        Connector.Write8(0x7E029D, 0x00);
        Connector.Write8(0x7E029E, 0x00);
        Connector.Write8(0x7E029F, 0x00);
        Connector.Write8(0x7E02A0, 0x00);
        Connector.Write8(0x7E02A1, 0x00);
        Connector.Write8(0x7E02A2, 0x00);
        Connector.Write8(0x7E02A3, 0x00);
        Connector.Write8(0x7E02A4, 0x00);
        Connector.Write8(0x7E02A5, 0x00);
        Connector.Write8(0x7E02A6, 0x00);
        Connector.Write8(0x7E02A7, 0x00);
        Connector.Write8(0x7E02A8, 0x00);
        Connector.Write8(0x7E02A9, 0x00);
        Connector.Write8(0x7E02AA, 0x00);
    }*/

    private void LockUI() => Connector.Write(0x7E029B, Enumerable.Repeat<byte>(0x01, 0x7E02AB - 0x7E029B).ToArray());
    /*{
        Connector.Write8(0x7E029B, 0x01);
        Connector.Write8(0x7E029C, 0x01);
        Connector.Write8(0x7E029D, 0x01);
        Connector.Write8(0x7E029E, 0x01);
        Connector.Write8(0x7E029F, 0x01);
        Connector.Write8(0x7E02A0, 0x01);
        Connector.Write8(0x7E02A1, 0x01);
        Connector.Write8(0x7E02A2, 0x01);
        Connector.Write8(0x7E02A3, 0x01);
        Connector.Write8(0x7E02A4, 0x01);
        Connector.Write8(0x7E02A5, 0x01);
        Connector.Write8(0x7E02A6, 0x01);
        Connector.Write8(0x7E02A7, 0x01);
        Connector.Write8(0x7E02A8, 0x01);
        Connector.Write8(0x7E02A9, 0x01);
        Connector.Write8(0x7E02AA, 0x01);
    }*/

    private bool StopAll()
    {
        bool result = true;
        ClearUI();
        result &= Connector.Write8(ADDR_SCREENSHAKE, 0x00);
        result &= Connector.Write8(ADDR_BUILD_ITEM, 0x00);
        result &= Connector.Write8(ADDR_DEMON_SEASON, 0x00);

        result &= Connector.Unfreeze(ADDR_POPULATION);
        result &= Connector.Unfreeze(ADDR_POPULATION + 2);
        result &= Connector.Unfreeze(ADDR_TIMER);
        result &= Connector.Unfreeze(0x7E0AB7);
        result &= Connector.Unfreeze(ADDR_OVERLAY_ACTIVE);
        result &= Connector.Unfreeze(ADDR_OVERLAY_MESSAGE);

        _forcebulldoze = false;
        _demonSeason = false;
        _shakescreen = false;
        return result;
    }

    protected override bool StopEffect(EffectRequest request)
    {
        switch (request.EffectID)
        {
            case "disaster_ufo":
            {
                if (Connector.Read8(0x7E0AF0, out byte a) && (a != 0x00)) return false;

                if (!Connector.Unfreeze(ADDR_POPULATION)) return false;
                if (!Connector.Unfreeze(ADDR_POPULATION + 2)) return false;
                if (!Connector.Unfreeze(ADDR_TIMER)) return false;
                if (!Connector.Unfreeze(0x7E0AB7)) return false;
                if (!Connector.Unfreeze(ADDR_OVERLAY_ACTIVE)) return false;
                if (!Connector.Unfreeze(ADDR_OVERLAY_MESSAGE)) return false;


                if (!Connector.Write8(ADDR_GAME_TYPE, gametype)) return false;
                if (!Connector.Write8(ADDR_SCENARIO, scenario)) return false;
                if (!Connector.Write16(ADDR_POPULATION, pop)) return false;
                if (!Connector.Write8(ADDR_POPULATION + 2, poph)) return false;
                if (!Connector.Write16(ADDR_TIMER, timer)) return false;

                Connector.SendMessage($"UFO END");

                return true;
            }
            case "demonseason":
            {
                bool result = Connector.Write8(ADDR_DEMON_SEASON, 0x00);
                if (result)
                {
                    Connector.SendMessage($"{request.DisplayViewer}'s demon season is over.");
                    _demonSeason = false;
                }
                return result;
            }
            case "forcebulldoze":
            {
                bool result = Connector.Unfreeze(ADDR_BUILD_ITEM) && Connector.Unfreeze(ADDR_BUILD_FORCE);
                if (result)
                {
                    Connector.SendMessage($"{request.DisplayViewer}'s forced bulldoze is over!");
                    ClearUI();
                    _forcebulldoze = false;
                }
                return result;
            }
            case "shakescreen":
            {
                bool result = Connector.Unfreeze(ADDR_SCREENSHAKE);
                if (result)
                {
                    Connector.SendMessage($"{request.DisplayViewer}'s screenshake is over!");
                    _shakescreen = false;
                }
                return result;
            }
            default:
                return true;
        }
    }

    public override bool StopAllEffects()
    {
        bool success = base.StopAllEffects(); ;
        try { success &= StopAll(); }
        catch { success = false; }
        return success;
    }
}