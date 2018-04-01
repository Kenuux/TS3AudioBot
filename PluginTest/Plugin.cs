using System;
using TS3AudioBot;
using TS3AudioBot.CommandSystem;
using TS3AudioBot.Plugins;
using TS3Client;
using TS3Client.Commands;
using TS3Client.Full;

namespace PluginTest
{
	public class Plugin : IBotPlugin
	{
		public void Dispose()
		{
			
		}

		public void Initialize()
		{
			
		}

		[Command("joschua", "Does some cool things!")]
		public static string CommandJoschua()
		{
			return "Hi";
		}
	}
}
