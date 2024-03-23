using System.ComponentModel;
using Terraria.ModLoader.Config;
// ReSharper disable once UnusedMember.Global
// ReSharper disable once FieldCanBeMadeReadOnly.Global
// ReSharper disable once ConvertToConstant.Global

namespace Roommates;

public class Config : ModConfig
{
	public override ConfigScope Mode => ConfigScope.ClientSide;

	[DefaultValue(false)]
	public bool requireChairs = false;
}