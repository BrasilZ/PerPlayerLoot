using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Terraria;
using TShockAPI;
using TerrariaApi.Server;

namespace PerPlayerLoot
{
    [ApiVersion(2, 1)]
    public class PPLPlugin : TerrariaPlugin
    {
        #region info
        public override string Name => "PerPlayerLoot";

        public override Version Version => new Version(2, 0);

        public override string Author => "Codian atualized by brasilzinhoz";

        public override string Description => "Duplicate loot chest inventories for each player.";
        #endregion

        public static FakeChestDatabase fakeChestDb;
        public static bool enablePpl = true;

        private const string ConfigPath = "tshock/perplayerloot_config.json";
        private PluginConfig config;

        public PPLPlugin(Main game) : base(game) { }

        public override void Initialize()
        {
            LoadConfig();
            InitializeDatabase();

            ServerApi.Hooks.GamePostInitialize.Register(this, OnWorldLoaded);
            ServerApi.Hooks.WorldSave.Register(this, OnWorldSave);

            TShockAPI.GetDataHandlers.PlaceChest += OnChestPlace;
            TShockAPI.GetDataHandlers.ChestOpen += OnChestOpen;
            TShockAPI.GetDataHandlers.ChestItemChange += OnChestItemChange;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ServerApi.Hooks.GamePostInitialize.Deregister(this, OnWorldLoaded);
                ServerApi.Hooks.WorldSave.Deregister(this, OnWorldSave);

                TShockAPI.GetDataHandlers.PlaceChest -= OnChestPlace;
                TShockAPI.GetDataHandlers.ChestOpen -= OnChestOpen;
                TShockAPI.GetDataHandlers.ChestItemChange -= OnChestItemChange;
            }

            base.Dispose(disposing);
        }

        private void LoadConfig()
        {
            if (!File.Exists(ConfigPath))
            {
                config = new PluginConfig();
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                var json = File.ReadAllText(ConfigPath);
                config = JsonSerializer.Deserialize<PluginConfig>(json) ?? new PluginConfig();
            }
        }

        private void InitializeDatabase()
        {
            if (config.DatabaseType.Equals("MySQL", StringComparison.OrdinalIgnoreCase))
            {
                string connectionString = $"Server={config.MySQL.Host};Port={config.MySQL.Port};Database={config.MySQL.Database};Uid={config.MySQL.Username};Pwd={config.MySQL.Password};";
                fakeChestDb = new FakeChestDatabase(connectionString, true);
                TSPlayer.Server.SendInfoMessage("Using MySQL database for per-player loot.");
            }
            else
            {
                fakeChestDb = new FakeChestDatabase("Data Source=tshock/perplayerloot.sqlite");
                TSPlayer.Server.SendInfoMessage("Using SQLite database for per-player loot.");
            }
        }

        private void OnWorldSave(WorldSaveEventArgs args)
        {
            fakeChestDb.SaveFakeChests();
        }

        private void OnWorldLoaded(EventArgs args)
        {
            fakeChestDb.Initialize();
            Commands.ChatCommands.Add(new Command("perplayerloot.toggle", ToggleCommand, "ppltoggle"));
        }

        private void ToggleCommand(CommandArgs args)
        {
            enablePpl = !enablePpl;
            if (enablePpl)
            {
                args.Player.SendSuccessMessage("Per player loot is now enabled!");
            }
            else
            {
                args.Player.SendSuccessMessage("Per player loot is now disabled! You can modify chests now and they will count as loot chests.");
            }
        }

        private void OnChestItemChange(object sender, GetDataHandlers.ChestItemEventArgs e)
        {
            if (!enablePpl) return;

            Chest realChest = Main.chest[e.ID];
            if (realChest == null) return;

            if (realChest.bankChest) return;

            if (fakeChestDb.IsChestPlayerPlaced(realChest.x, realChest.y)) return;

            Item item = new Item();
            item.netDefaults(e.Type);
            item.stack = e.Stacks;
            item.prefix = e.Prefix;

            Chest fakeChest = fakeChestDb.GetOrCreateFakeChest(e.ID, e.Player.UUID);
            fakeChest.item[e.Slot] = item;

            e.Handled = true;
        }

        private byte[] ConstructSpoofedChestItemPacket(int chestId, int slot, Item item)
        {
            MemoryStream memoryStream = new MemoryStream();
            OTAPI.PacketWriter packetWriter = new OTAPI.PacketWriter(memoryStream);

            packetWriter.BaseStream.Position = 0L;
            long position = packetWriter.BaseStream.Position;

            packetWriter.BaseStream.Position += 2L;
            packetWriter.Write((byte)PacketTypes.ChestItem);

            packetWriter.Write((short)chestId);
            packetWriter.Write((byte)slot);

            short netId = (short)item.netID;
            if (item.Name == null)
            {
                netId = 0;
            }

            packetWriter.Write((short)item.stack);
            packetWriter.Write(item.prefix);
            packetWriter.Write(netId);

            int positionAfter = (int)packetWriter.BaseStream.Position;

            packetWriter.BaseStream.Position = position;
            packetWriter.Write((ushort)positionAfter);
            packetWriter.BaseStream.Position = positionAfter;

            return memoryStream.ToArray();
        }

        private void OnChestOpen(object sender, GetDataHandlers.ChestOpenEventArgs e)
        {
            if (e.Handled) return;
            if (!enablePpl) return;

            int chestId = Chest.FindChest(e.X, e.Y);
            if (chestId == -1) return;

            Chest realChest = Main.chest[chestId];
            if (realChest == null) return;

            if (realChest.bankChest) return;

            if (fakeChestDb.IsChestPlayerPlaced(realChest.x, realChest.y)) return;

            Chest fakeChest = fakeChestDb.GetOrCreateFakeChest(chestId, e.Player.UUID);

            e.Player.SendInfoMessage("[c/FF0000:O loot neste baú é salvo por jogador!]");

            for (int slot = 0; slot < Chest.maxItems; slot++)
            {
                Item item = fakeChest.item[slot];
                byte[] payload = ConstructSpoofedChestItemPacket(chestId, slot, item);
                e.Player.SendRawData(payload);
            }

            e.Player.SendData(PacketTypes.ChestOpen, "", chestId);
            e.Player.ActiveChest = chestId;
            Main.player[e.Player.Index].chest = chestId;
            e.Player.SendData(PacketTypes.SyncPlayerChestIndex, null, e.Player.Index, chestId);

            e.Handled = true;
        }

        private void OnChestPlace(object sender, GetDataHandlers.PlaceChestEventArgs e)
        {
            if (!enablePpl) return;

            if (!fakeChestDb.IsChestPlayerPlaced(e.TileX, e.TileY - 1))
            {
                int chestId = Chest.FindChest(e.TileX, e.TileY - 1);
                if (chestId != -1)
                    Main.chest[chestId].item = new Item[Chest.maxItems];
            }

            fakeChestDb.SetChestPlayerPlaced(e.TileX, e.TileY - 1);
        }
    }

    public class PluginConfig
    {
        public string DatabaseType { get; set; } = "SQLite";
        public MySQLConfig MySQL { get; set; } = new MySQLConfig();
    }

    public class MySQLConfig
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 3306;
        public string Database { get; set; } = "perplayerloot";
        public string Username { get; set; } = "root";
        public string Password { get; set; } = "password";
    }
}