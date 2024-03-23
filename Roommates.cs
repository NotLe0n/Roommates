using System.Linq;
using System.Reflection;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace Roommates;
public class Roommates : Mod
{
	// so that ContentSamples.NpcsByNetId.TryGetValue(npc1ByType, out var value) returns false
	private const int MagicNumber = -999; // works unless Terraria adds an NPC with this ID
	
	public override void Load()
	{
		//On_TownRoomManager.CanNPCsLiveWithEachOther_int_NPC += (_, _, _, _) => true; inlined :(
		On_WorldGen.ScoreRoom_IsThisRoomOccupiedBySomeone += (orig, npc, _) => orig(npc, MagicNumber);
		On_WorldGen.IsRoomConsideredAlreadyOccupied += (orig, x, y, _) => orig(x, y, MagicNumber);
		On_WorldGen.IsRoomConsideredOccupiedForNPCIndex += (_, _) => true; // true so that canSpawn is set to true. Prevents NPCs from switching rooms
		On_WorldGen.RoomNeeds += RequireCorrectChairCount;
	}

	private static readonly FieldInfo RoomChairField = typeof(WorldGen).GetField("roomChair", BindingFlags.Static | BindingFlags.NonPublic);

	private static bool RequireCorrectChairCount(On_WorldGen.orig_RoomNeeds orig, int npctype)
	{
		bool ret = orig(npctype);
		if (!ModContent.GetInstance<Config>().requireChairs) {
			return ret;
		}

		if (Main.npc.First(x => x.type == npctype).housingCategory == HousingCategoryID.PetNPCs) {
			return ret;
		}

		int chairCount = CountChairs();
		int roommateCount = CountRoommates();
		
		bool roomChair = chairCount > roommateCount;
		RoomChairField.SetValue(null, roomChair);
		WorldGen.canSpawn = WorldGen.canSpawn && chairCount >= roommateCount;
		return ret && roomChair;
	}

	private static int CountChairs()
	{
		int numChairs = 0;
		for (int i = WorldGen.roomX1; i < WorldGen.roomX2; i++) {
			for (int j = WorldGen.roomY1; j < WorldGen.roomY2; j++) {
				if (Main.tile[i, j].TileType == TileID.Chairs) {
					numChairs++;
				}
			}
		}

		return numChairs;
	}

	private static int CountRoommates()
	{
		int numRoommates = 0;

		for (int k = 0; k < Main.maxNPCs; k++) {
			var n = Main.npc[k];
			if (!n.active || !n.townNPC || n.homeless || n.housingCategory == HousingCategoryID.PetNPCs) continue;
			if (WorldGen.roomX1-1 <= n.homeTileX && WorldGen.roomX2 >= n.homeTileX && WorldGen.roomY1 <= n.homeTileY && WorldGen.roomY2+1 >= n.homeTileY) {
				numRoommates++;
			}
		}

		return numRoommates;
	}
}