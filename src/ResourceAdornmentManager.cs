﻿// <copyright file="ResourceAdornmentManager.cs" company="Matt Lacey">
// Copyright (c) Matt Lacey. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace StringResourceVisualizer
{
    /// <summary>
    /// Important class. Handles creation of adornments on appropriate lines.
    /// </summary>
    internal class ResourceAdornmentManager : IDisposable
    {
        private readonly IAdornmentLayer layer;
        private readonly IWpfTextView view;
        private bool hasDoneInitialCreateVisualsPass = false;

        public ResourceAdornmentManager(IWpfTextView view)
        {
            this.view = view;
            this.layer = view.GetAdornmentLayer("StringResourceCommentLayer");

            this.view.LayoutChanged += this.LayoutChangedHandler;
        }

        public static List<string> ResourceFiles { get; set; } = new List<string>();

        public static List<string> SearchValues { get; set; } = new List<string>();

        public static List<(string path, XmlDocument xDoc)> XmlDocs { get; private set; } = new List<(string path, XmlDocument xDoc)>();

        public static bool ResourcesLoaded { get; private set; }

        // Initialize to the same default as VS
        public static uint TextSize { get; set; } = 10;

        // Initialize to a reasonable value for display on light or dark themes/background.
        public static Color TextForegroundColor { get; set; } = Colors.Gray;

        public static FileSystemWatcher ResxWatcher { get; private set; } = new FileSystemWatcher();

        public static string PreferredCulture { get; private set; }

        // Keep a record of displayed text blocks so we can remove them as soon as changed or no longer appropriate
        // Also use this to identify lines to pad so the textblocks can be seen
        public Dictionary<int, List<(TextBlock textBlock, string resName)>> DisplayedTextBlocks { get; set; } = new Dictionary<int, List<(TextBlock textBlock, string resName)>>();

        public static async Task LoadResourcesAsync(List<string> resxFilesOfInterest, string slnDirectory, string preferredCulture)
        {
            await TaskScheduler.Default;

            ResourcesLoaded = false;

            ResourceFiles.Clear();
            SearchValues.Clear();
            XmlDocs.Clear();

            // Store this as will need it when looking up which text to use in the adornment.
            PreferredCulture = preferredCulture;

            foreach (var resourceFile in resxFilesOfInterest)
            {
                await Task.Yield();

                try
                {
                    var doc = new XmlDocument();
                    doc.Load(resourceFile);

                    XmlDocs.Add((resourceFile, doc));
                    ResourceFiles.Add(resourceFile);

                    var searchTerm = $"{Path.GetFileNameWithoutExtension(resourceFile)}.";

                    if (!string.IsNullOrWhiteSpace(preferredCulture)
                     && searchTerm.EndsWith($"{preferredCulture}.", StringComparison.InvariantCultureIgnoreCase))
                    {
                        searchTerm = searchTerm.Substring(0, searchTerm.Length - preferredCulture.Length - 1);
                    }

                    if (!SearchValues.Contains(searchTerm))
                    {
                        SearchValues.Add(searchTerm);
                    }
                }
                catch (Exception e)
                {
                    await OutputPane.Instance.WriteAsync("Error loading resources");
                    await OutputPane.Instance.WriteAsync(e.Message);
                    await OutputPane.Instance.WriteAsync(e.Source);
                    await OutputPane.Instance.WriteAsync(e.StackTrace);
                }
            }

            if (resxFilesOfInterest.Any())
            {
                // Need to track changed and renamed events as VS doesn't do a direct overwrite but makes a temp file of the new version and then renames both files.
                // Changed event will also pick up changes made by extensions or programs other than VS.
                ResxWatcher.Filter = "*.resx";
                ResxWatcher.Path = slnDirectory;
                ResxWatcher.IncludeSubdirectories = true;
                ResxWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                ResxWatcher.Changed -= ResxWatcher_Changed;
                ResxWatcher.Changed += ResxWatcher_Changed;
                ResxWatcher.Renamed -= ResxWatcher_Renamed;
                ResxWatcher.Renamed += ResxWatcher_Renamed;
                ResxWatcher.EnableRaisingEvents = true;
            }
            else
            {
                ResxWatcher.EnableRaisingEvents = false;
                ResxWatcher.Changed -= ResxWatcher_Changed;
                ResxWatcher.Renamed -= ResxWatcher_Renamed;
            }

            ResourcesLoaded = true;
        }

        /// <summary>
        /// This is called by the TextView when closing. Events are unsubscribed here.
        /// </summary>
        /// <remarks>
        /// It's actually called twice - once by the IPropertyOwner instance, and again by the ITagger instance.
        /// </remarks>
        public void Dispose() => this.UnsubscribeFromViewerEvents();

        private static async void ResxWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            // Don't want to know about files being named from .resx to something else
            if (e.FullPath.EndsWith(".resx"))
            {
                await ReloadResourceFileAsync(e.FullPath);
            }
        }

        private static async void ResxWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            await ReloadResourceFileAsync(e.FullPath);
        }

        private static async Task ReloadResourceFileAsync(string filePath)
        {
            await OutputPane.Instance.WriteAsync($"(Re)loading {filePath}");

            const int maxAttemptCount = 5;
            const int baseWaitPeriod = 250;

            ResourcesLoaded = false;

            for (var i = 0; i < XmlDocs.Count; i++)
            {
                var (path, _) = XmlDocs[i];

                if (path == filePath)
                {
                    // File may still be locked after being moved/renamed/updated
                    // Allow for retry after delay with back-off.
                    for (var attempted = 0; attempted < maxAttemptCount; attempted++)
                    {
                        try
                        {
                            if (attempted > 0)
                            {
                                await Task.Delay(attempted * baseWaitPeriod);
                            }

                            var doc = new XmlDocument();
                            doc.Load(filePath);

                            XmlDocs[i] = (path, doc);
                        }
                        catch (Exception ex)
                        {
                            // If never load the changed file just stick with the previously loaded version.
                            // Hopefully get updated version after next change.
                            Debug.WriteLine(ex);
                        }
                    }

                    break;
                }
            }

            ResourcesLoaded = true;
        }

        /// <summary>
        /// On layout change add the adornment to any reformatted lines.
        /// </summary>
        private async void LayoutChangedHandler(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (ResourcesLoaded)
            {
                var collection = this.hasDoneInitialCreateVisualsPass ? (IEnumerable<ITextViewLine>)e.NewOrReformattedLines : this.view.TextViewLines;

                foreach (ITextViewLine line in collection)
                {
                    int lineNumber = line.Snapshot.GetLineFromPosition(line.Start.Position).LineNumber;

                    try
                    {
                        await this.CreateVisualsAsync(line, lineNumber);
                    }
                    catch (InvalidOperationException ex)
                    {
                        await OutputPane.Instance.WriteAsync("Error handling layout changed");
                        await OutputPane.Instance.WriteAsync(ex.Message);
                        await OutputPane.Instance.WriteAsync(ex.Source);
                        await OutputPane.Instance.WriteAsync(ex.StackTrace);
                    }

                    this.hasDoneInitialCreateVisualsPass = true;
                }
            }
        }

        /// <summary>
        /// Scans text line for use of resource class, then adds new adornment.
        /// </summary>
        private async Task CreateVisualsAsync(ITextViewLine line, int lineNumber)
        {
            try
            {
                string lineText = line.Extent.GetText();

                // The extent will include all of a collapsed section
                if (lineText.Contains(Environment.NewLine))
                {
                    // We only want the first "line" here as that's all that can be seen on screen
                    lineText = lineText.Substring(0, lineText.IndexOf(Environment.NewLine, StringComparison.InvariantCultureIgnoreCase));
                }

                // Remove any textblocks displayed on this line so it won't conflict with anything we add below.
                // Handles no textblocks to show or the text to display having changed.
                if (this.DisplayedTextBlocks.ContainsKey(lineNumber))
                {
                    foreach (var (textBlock, _) in this.DisplayedTextBlocks[lineNumber])
                    {
                        this.layer.RemoveAdornment(textBlock);
                    }

                    this.DisplayedTextBlocks.Remove(lineNumber);
                }

                var indexes = await lineText.GetAllIndexesAsync(SearchValues.ToArray());

                if (indexes.Any())
                {
                    var lastLeft = double.NaN;

                    // Reverse the list to can go through them right-to-left so know if there's anything that might overlap
                    indexes.Reverse();

                    foreach (var matchIndex in indexes)
                    {
                        var endPos = lineText.IndexOfAny(new[] { ' ', '.', ',', '"', '(', ')', '}', ';' }, lineText.IndexOf('.', matchIndex) + 1);

                        var foundText = endPos > matchIndex
                            ? lineText.Substring(matchIndex, endPos - matchIndex)
                            : lineText.Substring(matchIndex);

                        if (!this.DisplayedTextBlocks.ContainsKey(lineNumber))
                        {
                            this.DisplayedTextBlocks.Add(lineNumber, new List<(TextBlock textBlock, string resName)>());
                        }

                        string displayText = null;

                        if (ResourceFiles.Any())
                        {
                            var resourceName = foundText.Substring(foundText.IndexOf('.') + 1);
                            var fileBaseName = foundText.Substring(0, foundText.IndexOf('.'));

                            var doubleBreak = false;

                            foreach (var (path, xDoc) in this.GetDocsOfInterest(fileBaseName, XmlDocs, PreferredCulture))
                            {
                                foreach (XmlElement element in xDoc.GetElementsByTagName("data"))
                                {
                                    if (element.GetAttribute("name") == resourceName)
                                    {
                                        var valueElement = element.GetElementsByTagName("value").Item(0);
                                        displayText = valueElement?.InnerText;

                                        if (displayText != null)
                                        {
                                            var returnIndex = displayText.IndexOfAny(new[] { '\r', '\n' });

                                            if (returnIndex >= 0)
                                            {
                                                // Truncate at first wrapping character and add "Return Character" to indicate truncation
                                                displayText = displayText.Substring(0, returnIndex) + "⏎";
                                            }
                                        }

                                        // Not only have we found the value we want,
                                        // we also don't want to check any more files.
                                        doubleBreak = true;
                                        break;
                                    }
                                }

                                if (doubleBreak)
                                {
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(displayText) && TextSize > 0)
                        {
                            var brush = new SolidColorBrush(TextForegroundColor);
                            brush.Freeze();

                            const double textBlockSizeToFontScaleFactor = 1.4;

                            var tb = new TextBlock
                            {
                                Foreground = brush,
                                Text = $"\"{displayText}\"",
                                FontSize = TextSize,
                                Height = TextSize * textBlockSizeToFontScaleFactor,
                            };

                            this.DisplayedTextBlocks[lineNumber].Add((tb, foundText));

                            // Get coordinates of text
                            int start = line.Extent.Start.Position + matchIndex;
                            int end = line.Start + (line.Extent.Length - 1);
                            var span = new SnapshotSpan(this.view.TextSnapshot, Span.FromBounds(start, end));
                            var lineGeometry = this.view.TextViewLines.GetMarkerGeometry(span);

                            if (!double.IsNaN(lastLeft))
                            {
                                tb.MaxWidth = lastLeft - lineGeometry.Bounds.Left - 5; // Minus 5 for padding
                                tb.TextTrimming = TextTrimming.CharacterEllipsis;
                            }

                            Canvas.SetLeft(tb, lineGeometry.Bounds.Left);
                            Canvas.SetTop(tb, line.TextTop - tb.Height);

                            lastLeft = lineGeometry.Bounds.Left;

                            this.layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, line.Extent, tag: null, adornment: tb, removedCallback: null);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await OutputPane.Instance.WriteAsync("Error creating visuals");
                await OutputPane.Instance.WriteAsync(ex.Message);
                await OutputPane.Instance.WriteAsync(ex.Source);
                await OutputPane.Instance.WriteAsync(ex.StackTrace);
            }
        }

        private IEnumerable<(string path, XmlDocument xDoc)> GetDocsOfInterest(string resourceBaseName, List<(string path, XmlDocument xDoc)> xmlDocs, string preferredCulture)
        {
            // As may be multiple resource files, only check the ones which have the correct name.
            // If multiple projects in the solutions with same resource name (file & name), but different res value, the wrong value *may* be displayed
            // Get preferred cutlure files first
            if (!string.IsNullOrWhiteSpace(preferredCulture))
            {
                var cultureDocs = xmlDocs.Where(x => Path.GetFileNameWithoutExtension(x.path).Equals($"{resourceBaseName}.{preferredCulture}", StringComparison.InvariantCultureIgnoreCase));
                foreach (var item in cultureDocs)
                {
                    yield return item;
                }
            }

            // Then default resource files
            var defaultResources = xmlDocs.Where(x => Path.GetFileNameWithoutExtension(x.path).Equals(resourceBaseName));
            foreach (var item in defaultResources)
            {
                yield return item;
            }
        }

        private void UnsubscribeFromViewerEvents()
        {
            this.view.LayoutChanged -= this.LayoutChangedHandler;
        }
    }
}
