// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3AudioBot.Web.Api
{
	using CommandSystem;

	public class JsonError : JsonObject
	{
		private readonly CommandExceptionReason reason;
		public int ErrorCode => (int)reason;
		public string ErrorName => reason.ToString();
		public string ErrorMessage { get; }

		public JsonError(string msg, CommandExceptionReason reason) : base(msg)
		{
			ErrorMessage = msg;
			this.reason = reason;
		}
	}
}
