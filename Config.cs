using System.ComponentModel;
using Terraria.ModLoader.Config;
// ReSharper disable UnusedMember.Global
// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable ConvertToConstant.Global
// ReSharper disable UnassignedField.Global

namespace Roommates;

public class Config : ModConfig
{
	public override ConfigScope Mode => ConfigScope.ServerSide;

	[DefaultValue(false)]
	public bool requireChairs = false;

	[Slider]
	[Range(0, 20)]
	[DefaultValue(0)]
	public int maxRoommateCount;
}