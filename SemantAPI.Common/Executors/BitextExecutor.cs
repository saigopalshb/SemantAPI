﻿// <copyright file="BitextExecutor.cs" company="13th Parish">
// Copyright (c) 13th Parish 2013 All Rights Reserved
// </copyright>
// <author>George Kozlov (george.kozlov@outlook.com)</author>
// <date>03/30/2013</date>
// <summary>BytextSentiment and BitextExecutor classes</summary>

using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Linq;
using System.Xml.Serialization;

namespace SemantAPI.Common.Executors
{
	#region Serialization type

	[XmlRootAttribute("RESULT")]
	public sealed class BitextSentiment
	{
		[XmlElementAttribute("BLOCK")]
		public List<BitextSentenceSentiment> Blocks { get; set; }
	}

	[XmlRootAttribute("BLOCK")]
	public sealed class BitextSentenceSentiment
	{
		[XmlElementAttribute("ID")]
		public string Id { get; set; }

		[XmlElementAttribute("GLOBAL_VALUE")]
		public double Value { get; set; }

		[XmlElementAttribute("TEXT")]
		public string Text { get; set; }
	}

	#endregion

	public sealed class BitextExecutor : IExecutor
	{
		#region Private members

		AnalysisExecutionContext _context = null;

		#endregion

		#region Constructor

		public BitextExecutor()
		{
		}

		#endregion

		#region Private methods

		private string FormatParameters(Dictionary<string, string> parameters)
		{
			var paramsBuilder = new StringBuilder();
			var counter = 0;

			foreach (KeyValuePair<string, string> pair in parameters)
			{
				paramsBuilder.AppendFormat("{0}={1}", pair.Key, pair.Value);

				if (counter != parameters.Count - 1)
					paramsBuilder.Append("&");

				counter++;
			}

			return paramsBuilder.ToString();
		}

		private double MergeSentimentScore(BitextSentiment sentiment)
		{
			return sentiment.Blocks.Average(item => item.Value);
		}

		private string GetSentimentPolarity(double score)
		{
			if (score < 0)
				return "negative";
			else if (score > 0)
				return "positive";

			return "neutral";
		}

		#endregion

		#region Public methods and properties

		public void Execute(AnalysisExecutionContext context)
		{
			_context = context;

			if (context.Results.Count <= 0)
			{
				context.OnExecutionProgress("Bitext", new AnalysisExecutionProgressEventArgs(AnalysisExecutionStatus.Canceled, 0, 0, 0));
				return;
			}

			int processed = 0;
			int failed = 0;
			foreach (KeyValuePair<string, ResultSet> document in context.Results)
			{
				if (document.Value.Source.Length > 8192)
				{
					failed++;
					document.Value.AddOutput("Bitext", 0, "failed");

					AnalysisExecutionProgressEventArgs ea = new AnalysisExecutionProgressEventArgs(AnalysisExecutionStatus.Failed, context.Results.Count, processed, failed);
					context.OnExecutionProgress("Bitext", ea);

					if (ea.Cancel)
						break;

					continue;
				}

				Dictionary<string, string> parameters = new Dictionary<string, string>();
				parameters.Add("User", context.Key);
				parameters.Add("Pass", context.Secret);
				parameters.Add("OutFormat", context.Format.ToString());
				parameters.Add("Detail", "Global");
				parameters.Add("Normalized", "No");
				parameters.Add("Theme", "Gen");
				parameters.Add("ID", document.Key);
				parameters.Add("Lang", LocaleHelper.GetStupidLanguageAbbreviation(context.Language));
				parameters.Add("Text", HttpUtility.UrlEncode(document.Value.Source));

				byte[] data = Encoding.UTF8.GetBytes(FormatParameters(parameters));
				WebRequest request = WebRequest.Create("http://svc9.bitext.com/WS_NOps_Val/Service.aspx");
				request.ContentType = "application/x-www-form-urlencoded";
				request.Method = "POST";
				request.ContentLength = data.Length;

				using (Stream writer = request.GetRequestStream())
				{
					writer.Write(data, 0, data.Length);
					writer.Flush();
				}

				try
				{
					HttpWebResponse response = null;
					if (context.UseDebugMode)
					{
						TimeSpan time = TimeSpan.Zero;
						response = BenchmarkHelper.Invoke(new InvokeBenchmarkHandler(delegate(object state)
						{
							return request.GetResponse();
						}), null, out time) as HttpWebResponse;

						Console.WriteLine("Bitext: Sentiment for the document {0} has been retreived. Execution time is: {1}", document.Key, time.TotalMilliseconds);
					}
					else
						response = request.GetResponse() as HttpWebResponse;

					if (response.StatusCode != HttpStatusCode.Accepted && response.StatusCode != HttpStatusCode.OK)
					{
						failed++;
						document.Value.AddOutput("Bitext", 0, "failed");

						AnalysisExecutionProgressEventArgs ea = new AnalysisExecutionProgressEventArgs(AnalysisExecutionStatus.Processed, context.Results.Count, processed, failed);
						context.OnExecutionProgress("Bitext", ea);
						response.Close();

						if (ea.Cancel)
							break;
					}
					else
					{
						using (StreamReader reader = new StreamReader(response.GetResponseStream()))
						{
							string result = reader.ReadToEnd();
							result = result.Replace("\r\n", string.Empty)
								.Replace("\r", string.Empty)
								.Replace("\n", string.Empty)
								.Replace(">\"", ">")
								.Replace("\"<", "<");

							Regex regex = new Regex(@"(?<=\bencoding="")[^""]*");
							Match match = regex.Match(result);

							Encoding encoding = null;
							if (match.Success)
								encoding = Encoding.GetEncoding(match.Value);
							else
								encoding = Encoding.UTF8;

							BitextSentiment sentiment = null;
							using (Stream stream = new MemoryStream(encoding.GetBytes(result)))
							{
								XmlSerializer serializer = new XmlSerializer(typeof(BitextSentiment));
								sentiment = (BitextSentiment)serializer.Deserialize(stream);
							}

							processed++;
							double score = MergeSentimentScore(sentiment);
							string polarity = GetSentimentPolarity(score);
							document.Value.AddOutput("Bitext", score, polarity);
							AnalysisExecutionProgressEventArgs ea = new AnalysisExecutionProgressEventArgs(AnalysisExecutionStatus.Processed, context.Results.Count, processed, failed);
							context.OnExecutionProgress("Bitext", ea);

							if (ea.Cancel)
								break;
						}
					}
				}
				catch (Exception ex)
				{
					failed++;
					document.Value.AddOutput("Bitext", 0, "failed");
					AnalysisExecutionProgressEventArgs ea = new AnalysisExecutionProgressEventArgs(AnalysisExecutionStatus.Failed, context.Results.Count, processed, failed);
					ea.Reason = ex.Message;
					context.OnExecutionProgress("Bitext", ea);

					if (ea.Cancel)
						break;
				}
			}

			context.OnExecutionProgress("Bitext", new AnalysisExecutionProgressEventArgs(AnalysisExecutionStatus.Success, context.Results.Count, processed, failed));
		}


		public AnalysisExecutionContext Context
		{
			get { return _context; }
		}

		#endregion
	}
}
