using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace Roommates;

public class Roommates : Mod
{
	// so that ContentSamples.NpcsByNetId.TryGetValue(npc1ByType, out var value) returns false
	private const int MagicNumber = -999; // works unless Terraria adds an NPC with this ID

	public override void Load()
	{
		//On_TownRoomManager.CanNPCsLiveWithEachOther_int_NPC += (_, _, _, _) => true; inlined :(
		On_WorldGen.ScoreRoom_IsThisRoomOccupiedBySomeone += (orig, npc, npcTypeAsking) => orig(npc, MagicNumber) || OverLimit(npc, npcTypeAsking);
		On_WorldGen.IsRoomConsideredAlreadyOccupied += (orig, x, y, npcTypeToSpawn) => orig(x, y, MagicNumber) || OverLimit(npcTypeToSpawn: npcTypeToSpawn);
		On_WorldGen.IsRoomConsideredOccupiedForNPCIndex += (_, _) => true; // true so that canSpawn is set to true. Prevents NPCs from switching rooms
		On_WorldGen.RoomNeeds += RequireCorrectChairCount;
		On_NPC.AI_007_FindGoodRestingSpot += On_NPCOnAI_007_FindGoodRestingSpot;
		IL_NPC.AI_007_FindGoodRestingSpot += IL_NPCOnAI_007_FindGoodRestingSpot;
	}

	[CanBeNull] private static readonly MethodInfo TileGetType = typeof(Tile).GetProperty("type", BindingFlags.NonPublic | BindingFlags.Instance)?.GetMethod;
	
	private void IL_NPCOnAI_007_FindGoodRestingSpot(ILContext il)
	{
		var c = new ILCursor(il);
		
		/*
			C#: if (tile != null && tile.active() && TileID.Sets.CanBeSatOnForNPCs[tile.type])
																				here -------^
			IL:
				IL_010a: ldsfld bool[] Terraria.ID.TileID/Sets::CanBeSatOnForNPCs
				IL_010f: ldloca.s 11
				IL_0111: call instance uint16& Terraria.Tile::get_type()
				IL_0116: ldind.u2
				IL_0117: ldelem.u1
				IL_0118: brfalse.s IL_0157
				
					<--- here
				[+] ldarg.0
				[+] ldloc 9
				[+] ldloc 10
				[+] call delegate
				[+] brfalse.s IL_0157
		*/
		
		ILLabel falseLabel = null;
		if (!c.TryGotoNext(MoveType.After,
			    x => x.MatchLdsfld(typeof(TileID.Sets), "CanBeSatOnForNPCs"),
			    x => x.Match(OpCodes.Ldloca_S),
			    x => x.MatchCall(TileGetType ?? throw new MissingMethodException("match `call get_type()` failed")),
			    x => x.MatchLdindU2(),
			    x => x.MatchLdelemU1(),
			    x => x.MatchBrfalse(out falseLabel))) {
			throw new("IL_NPCOnAI_007_FindGoodRestingSpot failed");
		}

		c.EmitLdarg0();
		c.EmitLdloc(9);
		c.EmitLdloc(10);
		c.EmitDelegate((NPC self, int x, int y) =>
		{
			Point seat = new Point(x, y);
			Tile t = Main.tile[x, y];
			
			// Source: See NPC.AI_007_FindGoodRestingSpot
			if (t.TileType is TileID.Toilets or TileID.Chairs)
			{
				if (t.TileFrameY % 40 != 0) seat.Y--;
				seat.Y += 2;
			}
			else if (t.TileType >= TileID.Count) // modded sittable tile
			{
				var info = new TileRestingInfo(self, seat, Vector2.Zero, self.direction);
				TileLoader.ModifySittingTargetInfo(seat.X, seat.Y, t.TileType, ref info);
				seat = info.AnchorTilePosition;
				seat.Y += 1; // Set to tile *below* chair
			}

			// Reject the chair if any *other* town NPC is already seated in it. Adapted from NPC.AI_007_FindGoodRestingSpot
			for (int j = 0; j < Main.maxNPCs; j++)
			{
				NPC other = Main.npc[j];
				if (other.active && other.whoAmI != self.whoAmI
				    && other.aiStyle == NPCAIStyleID.Passive && other.townNPC && other.ai[0] == 5f
				    && (other.Bottom + Vector2.UnitY * -2f).ToTileCoordinates() == seat)
					return false; // occupied -> skip
			}
			
			foreach (var kv in ChairClaims) {
				if (kv.Key != self.whoAmI && kv.Value == seat) {
					NPC owner = Main.npc[kv.Key];
					if (owner.active && owner.townNPC && !Main.dayTime)
						return false;
				}
			}
			
			return true;
		});
		
		c.EmitBrfalse(falseLabel);
	}

	public static readonly Dictionary<int, Point> ChairClaims = new();

	private void On_NPCOnAI_007_FindGoodRestingSpot(On_NPC.orig_AI_007_FindGoodRestingSpot orig, NPC self, int myTileX, int myTileY, out int floorX, out int floorY)
	{
		orig(self, myTileX, myTileY, out floorX, out floorY);
		
		if (Main.dayTime) ChairClaims.Remove(self.whoAmI); // clear claims
		else ChairClaims[self.whoAmI] = new Point(floorX, floorY); // claim chair
	}

	private static readonly FieldInfo RoomChairField = typeof(WorldGen).GetField("roomChair", BindingFlags.Static | BindingFlags.NonPublic);

	private static bool RequireCorrectChairCount(On_WorldGen.orig_RoomNeeds orig, int npcType)
	{
		bool ret = orig(npcType);
		if (!ModContent.GetInstance<Config>().requireChairs) {
			return ret;
		}

		int chairCount = CountChairs();
		int roommateCount = CountRoommates();

		bool roomChair = chairCount > roommateCount;
		RoomChairField.SetValue(null, roomChair);
		WorldGen.canSpawn = WorldGen.canSpawn && chairCount >= roommateCount; // different condition to prevent NPCs from switching rooms
		return ret && roomChair;
	}

	private static int CountChairs()
	{
		float numChairs = 0;

		for (int i = WorldGen.roomX1; i <= WorldGen.roomX2; i++) {
			for (int j = WorldGen.roomY1; j <= WorldGen.roomY2; j++) {
				if (TileID.Sets.RoomNeeds.CountsAsChair.Contains(Main.tile[i, j].TileType)) {
					var tileData = TileObjectData.GetTileData(Main.tile[i, j]);
					numChairs += 1f / (tileData.Width * tileData.Height);
				}
			}
		}

		return (int)Math.Round(numChairs);
	}

	private static int CountRoommates(int ignoreNpc = -1)
	{
		int numRoommates = 0;

		for (int k = 0; k < Main.maxNPCs; k++) {
			var n = Main.npc[k];
			// don't count pets and ignore the npc currently counting, to prevent it from moving out
			if (!n.active || !n.townNPC || n.homeless || n.housingCategory == HousingCategoryID.PetNPCs || k == ignoreNpc) continue;
			
			if (WorldGen.roomX1 - 1 <= n.homeTileX && WorldGen.roomX2 >= n.homeTileX && WorldGen.roomY1 <= n.homeTileY && WorldGen.roomY2 + 1 >= n.homeTileY) {
				numRoommates++;
			}
		}

		return numRoommates;
	}

	private static bool OverLimit(int ignoreNpc = -1, int npcTypeToSpawn = -1)
	{
		int max = ModContent.GetInstance<Config>().maxRoommateCount;
		if (max == 0) return false;
		// allows pets to live in the house even if the limit is reached
		if (NPCID.Sets.IsTownPet[npcTypeToSpawn]) {
			return false;
		}

		return CountRoommates(ignoreNpc) >= max;
	}
}