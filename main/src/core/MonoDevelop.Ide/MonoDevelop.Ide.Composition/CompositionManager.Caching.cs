﻿//
// CompositionManager.Caching.cs
//
// Author:
//       Marius Ungureanu <maungu@microsoft.com>
//
// Copyright (c) 2018 
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Serialization;
using Microsoft.VisualStudio.Composition;
using Mono.Addins;
using MonoDevelop.Core;
using MonoDevelop.Core.AddIns;
using MonoDevelop.Core.Instrumentation;

namespace MonoDevelop.Ide.Composition
{
	public partial class CompositionManager
	{
		internal class Caching
		{
			Task saveTask;
			public HashSet<Assembly> Assemblies { get; }
			readonly string mefCacheFile;
			readonly string mefCacheControlFile;

			public Caching (HashSet<Assembly> assemblies, Func<string, string> getCacheFilePath = null)
			{
				Assemblies = assemblies;

				getCacheFilePath = getCacheFilePath ?? (file => Path.Combine (AddinManager.CurrentAddin.PrivateDataPath, file));
				mefCacheFile = getCacheFilePath ("mef-cache");
				mefCacheControlFile = getCacheFilePath ("mef-cache-control");
			}

			void IdeApp_Exiting (object sender, ExitEventArgs args)
			{
				// As of the time this code was written, serializing the cache takes 200ms.
				// Maybe show a dialog and progress bar here that we're closing after save.
				// We cannot cancel the save, vs-mef doesn't use the cancellation tokens in the API.
				saveTask?.Wait ();
			}

			internal Stream OpenCacheStream () => File.Open (mefCacheFile, FileMode.Open);

			internal bool CanUse ()
			{
				// If we don't have a control file, bail early
				if (!File.Exists (mefCacheControlFile))
					return false;

				using (var timer = Counters.CompositionCacheControl.BeginTiming ()) {
					// Read the cache from disk
					var serializer = new XmlSerializer (typeof (MefControlCache));
					MefControlCache controlCache;
					using (var fs = File.Open (mefCacheControlFile, FileMode.Open)) {
						controlCache = (MefControlCache)serializer.Deserialize (fs);
					}

					// Short-circuit on number of assemblies change
					if (controlCache.AssemblyInfos.Length != Assemblies.Count)
						return false;

					// Validate that the assemblies match and we have the same time stamps on them.
					var currentAssemblies = new HashSet<string> (Assemblies.Select (asm => asm.Location));
					foreach (var assemblyInfo in controlCache.AssemblyInfos) {
						if (!currentAssemblies.Contains (assemblyInfo.Location))
							return false;

						if (File.GetLastWriteTimeUtc (assemblyInfo.Location) != assemblyInfo.LastWriteTimeUtc)
							return false;
					}
				}

				return true;
			}

			internal Task Write (RuntimeComposition runtimeComposition, CachedComposition cacheManager)
			{
				IdeApp.Exiting += IdeApp_Exiting;

				return saveTask = Task.Run (() => WriteMefCache (runtimeComposition, cacheManager)).ContinueWith (t => {
					IdeApp.Exiting -= IdeApp_Exiting;
					saveTask = null;

					if (t.IsFaulted) {
						LoggingService.LogError ("Failed to write MEF cached", t.Exception.Flatten ());
					}
				});
			}

			internal async Task WriteMefCache (RuntimeComposition runtimeComposition, CachedComposition cacheManager)
			{
				using (var timer = Counters.CompositionSave.BeginTiming ()) {
					WriteMefCacheControl (timer);

					// Serialize the MEF cache.
					using (var stream = File.Open (mefCacheFile, FileMode.Create)) {
						await cacheManager.SaveAsync (runtimeComposition, stream);
					}
				}
			}

			void WriteMefCacheControl (ITimeTracker timer)
			{
				// Create cache control data.
				var controlCache = new MefControlCache {
					AssemblyInfos = Assemblies.Select (asm => new MefControlCacheAssemblyInfo {
						Location = asm.Location,
						LastWriteTimeUtc = File.GetLastWriteTimeUtc (asm.Location),
					}).ToArray (),
				};

				// Serialize it to disk
				var serializer = new XmlSerializer (typeof (MefControlCache));
				using (var fs = File.Open (mefCacheControlFile, FileMode.Create)) {
					serializer.Serialize (fs, controlCache);
				}
				timer.Trace ("Composition control file written");
			}
		}

		// FIXME: Don't ship these as public
		[Serializable]
		public class MefControlCache
		{
			public MefControlCacheAssemblyInfo [] AssemblyInfos;
		}

		[Serializable]
		public class MefControlCacheAssemblyInfo
		{
			public string Location;
			public DateTime LastWriteTimeUtc;
		}
	}
}