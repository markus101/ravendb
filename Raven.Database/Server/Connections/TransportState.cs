﻿// -----------------------------------------------------------------------
//  <copyright file="TransportState.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Raven.Abstractions.Data;
using Raven.Database.Server.SignalR;
using Raven.Database.Util;
using System.Linq;

namespace Raven.Database.Server.Connections
{
	public class TransportState
	{
		readonly TimeSensitiveStore<string> timeSensitiveStore = new TimeSensitiveStore<string>(TimeSpan.FromSeconds(45));

		readonly ConcurrentDictionary<string, ConnectionState> connections = new ConcurrentDictionary<string, ConnectionState>();

		public TimeSensitiveStore<string> TimeSensitiveStore
		{
			get { return timeSensitiveStore; }
		}

		public void OnIdle()
		{
			ConnectionState _;
			timeSensitiveStore.ForAllExpired(s => connections.TryRemove(s, out _));
		}

		public ConnectionState Register(EventsTransport transport)
		{
			timeSensitiveStore.Seen(transport.Id);
			transport.Disconnected += () => TimeSensitiveStore.Missing(transport.Id);
			return connections.AddOrUpdate(transport.Id, new ConnectionState(transport), (s, state) =>
			                                                                             	{
			                                                                             		state.Reconnect(transport);
			                                                                             		return state;
			                                                                             	});
		}

		public event Action<object, IndexChangeNotification> OnIndexChangeNotification = delegate { }; 

		public void Send(IndexChangeNotification indexChangeNotification)
		{
			OnIndexChangeNotification(this, indexChangeNotification);
			foreach (var connectionState in connections)
			{
				connectionState.Value.Send(indexChangeNotification);
			}
		}

		public event Action<object, DocumentChangeNotification> OnDocumentChangeNotification = delegate { }; 

		public void Send(DocumentChangeNotification documentChangeNotification)
		{
			OnDocumentChangeNotification(this, documentChangeNotification);
			foreach (var connectionState in connections)
			{
				connectionState.Value.Send(documentChangeNotification);
			}
		}

		public ConnectionState For(string id)
		{
			return connections.GetOrAdd(id, _ =>
			                                	{
			                                		var connectionState = new ConnectionState(null);
			                                		TimeSensitiveStore.Missing(id);
			                                		return connectionState;
			                                	});
		}
	}

	public class ConnectionState
	{
		private readonly ConcurrentSet<string> matchingIndexes =
			new ConcurrentSet<string>(StringComparer.InvariantCultureIgnoreCase);
		private readonly ConcurrentSet<string> matchingDocuments =
			new ConcurrentSet<string>(StringComparer.InvariantCultureIgnoreCase);

		private readonly ConcurrentSet<string> matchingDocumentPrefixes =
			new ConcurrentSet<string>(StringComparer.InvariantCultureIgnoreCase);

		private readonly ConcurrentQueue<object> pendingMessages = new ConcurrentQueue<object>();

		private EventsTransport eventsTransport;

		private int watchAllDocuments;

		public ConnectionState(EventsTransport eventsTransport)
		{
			this.eventsTransport = eventsTransport;
		}
		
		public void WatchIndex(string name)
		{
			matchingIndexes.TryAdd(name);
		}

		public void UnwatchIndex(string name)
		{
			matchingIndexes.TryRemove(name);
		}

		public void Send(DocumentChangeNotification documentChangeNotification)
		{
			var value = new { Value = documentChangeNotification, Type = "DocumentChangeNotification" };
			if (watchAllDocuments > 0)
			{
				Enqueue(value);
				return;
			}

			if(matchingDocuments.Contains(documentChangeNotification.Name))
			{
				Enqueue(value);
				return;
			}

			var hasPrefix = matchingDocumentPrefixes.Any(x => documentChangeNotification.Name.StartsWith(x, StringComparison.InvariantCultureIgnoreCase));
			if (hasPrefix == false)
				return;

			Enqueue(value);
		}

		public void Send(IndexChangeNotification indexChangeNotification)
		{
			if (matchingIndexes.Contains(indexChangeNotification.Name) == false)
				return;


			Enqueue(new { Value = indexChangeNotification, Type = "IndexChangeNotification" });
		}

		private void Enqueue(object msg)
		{
			if (eventsTransport == null || eventsTransport.Connected == false)
			{
				pendingMessages.Enqueue(msg);
				return;
			}

			eventsTransport.SendAsync(msg)
				.ContinueWith(task =>
				              	{
				              		if (task.IsFaulted == false)
				              			return;
				              		pendingMessages.Enqueue(msg);
				              	});
		}

		public void WatchAllDocuments()
		{
			Interlocked.Increment(ref watchAllDocuments);
		}

		public void UnwatchAllDocuments()
		{
			Interlocked.Decrement(ref watchAllDocuments);
		}

		public void WatchDocument(string name)
		{
			matchingDocuments.TryAdd(name);
		}

		public void UnwatchDocument(string name)
		{
			matchingDocuments.TryRemove(name);
		}

		public void WatchDocumentPrefix(string name)
		{
			matchingDocumentPrefixes.TryAdd(name);
		}

		public void UnwatchDocumentPrefix(string name)
		{
			matchingDocumentPrefixes.TryRemove(name);
		}

		public void Reconnect(EventsTransport transport)
		{
			eventsTransport = transport;
			var items = new List<object>();
			object result;
			while (pendingMessages.TryDequeue(out result))
			{
				items.Add(result);
			}

			eventsTransport.SendManyAsync(items)
				.ContinueWith(task =>
				{
					if (task.IsFaulted == false)
						return;
					foreach (var item in items)
					{
						pendingMessages.Enqueue(item);
					}
				});
		}
	}
}