using System;
using System.Collections.Concurrent;
using Verse;

namespace VerbalCommands
{
	// Simple cross-thread hand-off queue. Background threads (the HTTP call) never touch
	// game state directly; they Post an Action here, and the main thread drains the queue
	// once per frame from GameComponent_VerbalCommands.GameComponentUpdate().
	public static class MainThread
	{
		private static readonly ConcurrentQueue<Action> queue = new ConcurrentQueue<Action>();

		public static void Post(Action a)
		{
			if (a == null)
			{
				return;
			}
			queue.Enqueue(a);
		}

		public static void Drain()
		{
			while (queue.TryDequeue(out Action a))
			{
				try
				{
					a();
				}
				catch (Exception ex)
				{
					Log.Error("[VerbalCommands] Exception in MainThread.Drain: " + ex);
				}
			}
		}
	}
}
