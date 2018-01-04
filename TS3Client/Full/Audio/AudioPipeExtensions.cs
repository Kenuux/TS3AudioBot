// TS3Client - A free TeamSpeak3 client implementation
// Copyright (C) 2017  TS3Client contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3Client.Full.Audio
{
	using System;

	public static class AudioPipeExtensions
	{
		public static T Chain<T>(this IAudioActiveProducer producer, T addConsumer) where T : IAudioPassiveConsumer
		{
			if (producer.OutStream == null)
			{
				producer.OutStream = addConsumer;
			}
			else if (producer is SplitterPipe splitter)
			{
				splitter.Add(addConsumer);
			}
			else
			{
				splitter = new SplitterPipe();
				splitter.Add(addConsumer);
				splitter.Add(producer.OutStream);
				producer.OutStream = splitter;
			}
			return addConsumer;
		}

		public static T Chain<T>(this IAudioActiveProducer producer, Action<T> init = null) where T : IAudioPassiveConsumer, new()
		{
			var addConsumer = new T();
			init?.Invoke(addConsumer);
			return producer.Chain(addConsumer);
		}
	}
}
