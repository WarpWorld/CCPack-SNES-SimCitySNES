using System;
using System.Collections.Generic;
using System.Linq;
using CrowdControl.Common;
using JetBrains.Annotations;


namespace CrowdControl.Games.Packs
{
    [UsedImplicitly]
    public class SimCitySNES : SNESEffectPack
    {
        [NotNull]
        private readonly IPlayer _player;

        public SimCitySNES([NotNull] IPlayer player, [NotNull] Func<CrowdControlBlock, bool> responseHandler, [NotNull] Action<object> statusUpdateHandler) : base(responseHandler, statusUpdateHandler) => _player = player;

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
        private const uint ADDR_GAMESTATE = 0x7E1FF6; //poke FF to gameover
        private const uint ADDR_GAMESPEED = 0x7E0193; //00 fastest - 03 stop
        private const uint ADDR_DISASTER = 0x7E0197;

        private const uint ADDR_CURSOR_X = 0x7E01EB;
        private const uint ADDR_CURSOR_Y = 0x7E01ED;

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

        private class GiftAssociation
        {
            public string GiftItem { get; }
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
            public string DisasterItem { get; }
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
            public string BuildingItem { get; }
            public string BuildingName { get; }
            public byte BuildingID { get; }
            public uint BuildingUIAddress { get; }

            public BuildingAssociation(string buildingName, byte buildingID, uint buildingUI)
            {
                BuildingName = buildingName;
                BuildingID = buildingID;
                BuildingUIAddress = buildingUI;
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

        private Dictionary<string, GiftAssociation> _game_gifts = new Dictionary<string, GiftAssociation>(StringComparer.InvariantCultureIgnoreCase)
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

        private Dictionary<string, DisasterAssociation> _game_disasters = new Dictionary<string, DisasterAssociation>(StringComparer.InvariantCultureIgnoreCase)
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
            //Only the above I know how to trigger correctly, sadly setting the other unused flags do nothing
            //the below can be trigger sometimes by poking some additional bits but seem in-consistent 
            {"nuclear", new DisasterAssociation("Nuclear Explosion", 0x00, 0x24, 0x7E0A80, 0) },
            {"shipwreck", new DisasterAssociation("Shipwreck", 0x00, 0x2B, 0x7E0A96, 0) }, //this one spawns a ship in the bottom right and causes it to wreck if the map has no water
            {"ufo", new DisasterAssociation("UFO Attack", 0x00, 0x30, 0x7E0A80, 0) },
            {"disaster", new DisasterAssociation("Unknown Disaster", 0x00, 0x3D, 0x7E0A80, 0) } //This one just gives the warning. 
            //, 0x7E0A80 is a place holder to just let it trigger
        };

        private Dictionary<string, BuildingAssociation> _game_building = new Dictionary<string, BuildingAssociation>(StringComparer.InvariantCultureIgnoreCase)
        {
            //SafeName, BuildingName, BuildingID, BuildingUI
            {"bulldoze", new BuildingAssociation("Bulldoze", 0x00, 0x7E029B) },
            {"roads", new BuildingAssociation("Roads", 0x01, 0x7E029C) },
            {"rails", new BuildingAssociation("Rails", 0x02, 0x7E029D) },
            {"power", new BuildingAssociation("Powerlines", 0x03, 0x7E029E) },
            {"park", new BuildingAssociation("Park", 0x04, 0x7E029F) },
            {"residental", new BuildingAssociation("Residental", 0x05, 0x7E02A0) },
            {"commercial", new BuildingAssociation("Commercial", 0x06, 0x7E02A1) },
            {"industrial", new BuildingAssociation("Industrial", 0x07, 0x7E02A2) },
            {"policedept", new BuildingAssociation("Police Department", 0x08, 0x7E02A3) },
            {"firedept", new BuildingAssociation("Fire Station", 0x09, 0x7E02A4) },
            {"stadium", new BuildingAssociation("Stadium", 0x0A, 0x7E02A5) },
            {"seaport", new BuildingAssociation("Sea Port", 0x0B, 0x7E02A6) },
            {"coal", new BuildingAssociation("Coal Power", 0x0E, 0x7E02A7) },
            {"nuclear", new BuildingAssociation("Nuclear Power", 0x0D, 0x7E02A8) },
            {"airport", new BuildingAssociation("Airport", 0x0C, 0x7E02A9) }
        };

        private Dictionary<string, MessageAssociation> _game_messages = new Dictionary<string, MessageAssociation>(StringComparer.InvariantCultureIgnoreCase)
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


        public override List<Effect> Effects
        {
            get
            {
                List<Effect> effects = new List<Effect>
                {
                    //give and take sorta work. would like to give the player * 100 what the input is
                    //sometimes it doesnt take, not sure if its tripping up somewhere
                    new Effect("Give Money", "givemoney", new[] {"simCitySNESMoney"}),
                    new Effect("Take Money", "takemoney", new[] {"simCitySNESMoney"}),
                    new Effect("Choose a Disaster ", "disaster", ItemKind.Folder),
                    new Effect("Give gift of ", "present", ItemKind.Folder),
                    new Effect("Switch building item to ", "building", ItemKind.Folder),
                    new Effect("Send a helpful message ", "helpfulmessage", new[] {"simCitySNESHelpfulMessage"}),
                    new Effect("Increase Transport Funds", "increasetransport", new[] {"quantity99"}),
                    new Effect("Decrease Transport Funds", "decreasetransport", new[] {"quantity99"}),
                    new Effect("Increase Police Funds", "increasepolice", new[] {"quantity99"}),
                    new Effect("Decrease Police Funds", "decreasepolice", new[] {"quantity99"}),
                    new Effect("Increase Fire Funds", "increasefire", new[] {"quantity99"}),
                    new Effect("Decrease Fire Funds", "decreasefire", new[] {"quantity99"}),
                    new Effect("Enable Demon Season", "demonseason"),
                    new Effect("Change Month", "changemonth", new[] {"quantity99"}), //would be nice if this looped back? so if it was on 11 and i added 11, it would go to 10.
                    new Effect("Change Year", "changeyear", new[] {"quantity99"}),
                    new Effect("Game Over", "gameover"),
                    new Effect("Force Bulldoze (15 seconds)", "forcebulldoze"),
                    new Effect("Max Speed", "maxspeed"),
                    new Effect("Medium Speed", "mediumspeed"),
                    new Effect("Low Speed", "lowspeed"),
                    new Effect("Stop Time", "stoptime"),
                    new Effect("Enable Auto-Bulldoze", "enableautobulldoze"),
                    new Effect("Disable Auto-Bulldoze", "disableautobulldoze"),
                    new Effect("Enable Auto-Tax", "enableautotax"),
                    new Effect("Disable Auto-Tax", "disableautotax"),
                    new Effect("Enable Auto-Goto", "enableautogoto"),
                    new Effect("Disable Auto-Goto", "disableautogoto"),
                    new Effect("Shake the screen!", "shakescreen", new[] {"quantity9"})
                };

                effects.AddRange(_game_gifts.Take(15).Select(t => new Effect($"{t.Value.GiftName}", $"present_{t.Key}", "present")));
                effects.AddRange(_game_disasters.Take(6).Select(t => new Effect($"{t.Value.DisasterName}", $"disaster_{t.Key}", "disaster")));
                effects.AddRange(_game_building.Take(15).Select(t => new Effect($"{t.Value.BuildingName}", $"building_{t.Key}", "building")));
                effects.AddRange(_game_messages.Take(33).Select(t => new Effect($"{t.Value.MessageName}", t.Key, ItemKind.Usable, "simCitySNESHelpfulMessage")));

                return effects;
            }
        }

        public override List<ItemType> ItemTypes => new List<ItemType>(new[]
        {
            new ItemType("Quantity", "quantity99", ItemType.Subtype.Slider, "{\"min\":1,\"max\":99}"),
            new ItemType("Money x10", "simCitySNESMoney", ItemType.Subtype.Slider, "{\"min\":1,\"max\":9999}"),
            new ItemType("Message", "simCitySNESHelpMessage", ItemType.Subtype.ItemList),
            new ItemType("Quantity", "quantity9", ItemType.Subtype.Slider, "{\"min\":1,\"max\":9}")
        });

        public override List<ROMInfo> ROMTable => new List<ROMInfo>(new[]
        {
            new ROMInfo("SimCity (v1.0) (U) (Headered)", null, Patching.Ignore, ROMStatus.ValidPatched, s => Patching.MD5(s, "ee177068d94ede4c95ec540b0db255db")),
            new ROMInfo("SimCity (v1.0) (U) (Unheadered)", null, Patching.Ignore, ROMStatus.ValidPatched, s => Patching.MD5(s, "23715fc7ef700b3999384d5be20f4db5"))
        });

        public override List<(string, Action)> MenuActions => new List<(string, Action)>();

        public override Game Game { get; } = new Game(29, "Sim City", "SimCity", "SNES", ConnectorType.SNESConnector);

        protected override bool IsReady(EffectRequest request) => Connector.Read8(ADDR_GAMESTATE, out byte b) && (b == 0x00);

        protected override void RequestData(DataRequest request) => Respond(request, request.Key, null, false, $"Variable name \"{request.Key}\" not known");

        protected override void StartEffect(EffectRequest request)
        {
            if (!IsReady(request))
            {
                DelayEffect(request, TimeSpan.FromSeconds(5));
                return;
            }

            sbyte sign = 1;
            string[] codeParams = request.FinalCode.Split('_');
            switch (codeParams[0])
            {
                case "demonseason":
                    {
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
                        return;
                    }
                case "forcebulldoze":
                    {
                        /*StartTimed(request,
                            () => (!_forcebulldoze && Connector.Read8(ADDR_GAMESTATE, out byte b) && (b == 0x00)),
                            () =>
                            {
                                bool result = Connector.Freeze8(ADDR_BUILD_ITEM, 0x00) && Connector.Freeze8(ADDR_BUILD_FORCE, 0x01);
                                if (result)
                                {
                                    LockUI();
                                    _forcebulldoze = true;
                                    Connector.SendMessage($"{request.DisplayViewer} has enabled the forced bulldoze.");
                                }
                                return result;
                            },
                            TimeSpan.FromSeconds(20));*/
                        RepeatAction(request, TimeSpan.FromSeconds(20),
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
                            TimeSpan.FromSeconds(0.2), true).WhenCompleted.Then(t =>
                            {
                                Connector.SendMessage($"{request.DisplayViewer}'s forced bulldoze is over!");
                                ClearUI();
                                _forcebulldoze = false;
                            });
                        return;
                    }
                case "shakescreen":
                    {
                        long seconds = request.AllItems[1].Reduce(_player);
                        /*StartTimed(request,
                            () => (!_shakescreen && Connector.Read8(ADDR_GAMESTATE, out byte b) && (b == 0x00) && Connector.Read8(ADDR_SCREENSHAKE, out byte c) && (c == 0x00)),
                            () =>
                            {
                                bool result = Connector.Freeze8(ADDR_SCREENSHAKE, 0x01);
                                if (result)
                                {
                                    _shakescreen = true;
                                    Connector.SendMessage($"{request.DisplayViewer} shook your screen for {seconds} seconds!.");
                                }
                                return result;
                            },
                            TimeSpan.FromSeconds(seconds));*/
                        RepeatAction(request, TimeSpan.FromSeconds(seconds),
                            () => (!_shakescreen) && Connector.IsZero8(ADDR_GAMESTATE) && Connector.IsZero8(ADDR_SCREENSHAKE),
                            () => {
                                _shakescreen = true;
                                return Connector.SendMessage($"{request.DisplayViewer} shook your screen for {seconds} seconds!.");
                            }, TimeSpan.FromSeconds(2.5),
                            () => Connector.IsZero8(ADDR_GAMESTATE), TimeSpan.FromSeconds(1),
                            () => Connector.Write8(ADDR_SCREENSHAKE, 0x7F),
                            TimeSpan.FromSeconds(0.5), true).WhenCompleted.Then(t => {
                                Connector.SendMessage($"{request.DisplayViewer}'s screenshake is over!");
                                _shakescreen = false;
                            });
                        return;
                    }
                case "gameover":
                    {
                        TryEffect(request,
                            () => Connector.Write8(ADDR_GAMESTATE, 0xff),
                            () => true,
                            () =>
                            {
                                Connector.SendMessage($"{request.DisplayViewer} decided you had enough.");
                            });
                        return;
                    }
                case "enableautobulldoze":
                    {
                        byte previous;
                        TryEffect(request,
                            () => Connector.Read8(ADDR_OPTIONS, out byte b) && ((b & 0x01) == 0),
                            () => Connector.SetBits(ADDR_OPTIONS, 0x01, out previous),
                            () =>
                            {
                                Connector.SendMessage($"{request.DisplayViewer} enabled auto-bulldoze!");
                            });
                        return;
                    }
                case "disableautobulldoze":
                    {
                        byte previous;
                        TryEffect(request,
                            () => Connector.Read8(ADDR_OPTIONS, out byte b) && ((b & 0x01) == 1),
                            () => Connector.UnsetBits(ADDR_OPTIONS, 0x01, out previous),
                            () =>
                            {
                                Connector.SendMessage($"{request.DisplayViewer} disabled auto-bulldoze!");
                            });
                        return;
                    }
                case "enableautotax":
                    {
                        byte previous;
                        TryEffect(request,
                            () => Connector.Read8(ADDR_OPTIONS, out byte b) && ((b & 0x02) == 1),
                            () => Connector.SetBits(ADDR_OPTIONS, 0x02, out previous),
                            () =>
                            {
                                Connector.SendMessage($"{request.DisplayViewer} enabled auto-tax!");
                            });
                        return;
                    }
                case "disableautotax":
                    {
                        byte previous;
                        TryEffect(request,
                            () => Connector.Read8(ADDR_OPTIONS, out byte b) && ((b & 0x02) == 0),
                            () => Connector.UnsetBits(ADDR_OPTIONS, 0x02, out previous),
                            () =>
                            {
                                Connector.SendMessage($"{request.DisplayViewer} disabled auto-tax!");
                            });
                        return;
                    }
                case "enableautogoto":
                    {
                        byte previous;
                        TryEffect(request,
                            () => Connector.Read8(ADDR_OPTIONS, out byte b) && ((b & 0x04) == 0),
                            () => Connector.SetBits(ADDR_OPTIONS, 0x04, out previous),
                            () =>
                            {
                                Connector.SendMessage($"{request.DisplayViewer} enabled auto-goto disater!");
                            });
                        return;
                    }
                case "disableautogoto":
                    {
                        byte previous;
                        TryEffect(request,
                            () => Connector.Read8(ADDR_OPTIONS, out byte b) && ((b & 0x04) == 1),
                            () => Connector.UnsetBits(ADDR_OPTIONS, 0x04, out previous),
                            () =>
                            {
                                Connector.SendMessage($"{request.DisplayViewer} disabled auto-goto disaster!");
                            });
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
                        long money = request.AllItems[1].Reduce(_player) * 10;
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
                        byte tax = (byte)request.AllItems[1].Reduce(_player);
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
                        byte tax = (byte)request.AllItems[1].Reduce(_player);
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
                        byte tax = (byte)request.AllItems[1].Reduce(_player);
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
                        byte tax = (byte)request.AllItems[1].Reduce(_player);
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
                        byte tax = (byte)request.AllItems[1].Reduce(_player);
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
                        byte tax = (byte)request.AllItems[1].Reduce(_player);
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
                        byte toAdd = (byte)request.AllItems[1].Reduce(_player);
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
                        byte year = (byte)request.AllItems[1].Reduce(_player);
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
                    SendDisaster(request, dType.DisasterID, dType.DisasterAddress, dType.DisasterCheck, dType.DisasterName);
                    return;
                case "building":
                    var bType = _game_building[codeParams[1]];
                    SetBuilding(request, bType.BuildingID, bType.BuildingUIAddress, bType.BuildingName);
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

        private void SetBuilding(EffectRequest request, byte bType, ulong BuildingUIAddress, string buildingName)
        {

            TryEffect(request,
                () => Connector.Read8(ADDR_BUILD_ITEM, out byte b) && (b != bType),
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
            ClearUI();
            Connector.Write8(ADDR_SCREENSHAKE, 0x00);
            Connector.Write8(ADDR_BUILD_ITEM, 0x00);
            Connector.Write8(ADDR_DEMON_SEASON, 0x00);
            _forcebulldoze = false;
            _demonSeason = false;
            _shakescreen = false;
            return true;
        }

        protected override bool StopEffect(EffectRequest request)
        {
            switch (request.InventoryItem.BaseItem.Code)
            {
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
                    return false;
            }
        }

        public override bool StopAllEffects() => StopAll();
    }
}
