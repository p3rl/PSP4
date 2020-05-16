using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;

namespace PSP4
{
	internal static class P4Helper
	{
		public static List<string> InvokeP4(string workingDirectory, string cmd, List<string> arguments)
		{
			using (var process = new Process())
			{
				var result = new List<string>();

				process.StartInfo.FileName = "p4.exe";
				process.StartInfo.Arguments = $"{cmd} {string.Join(" ", arguments)}";
				process.StartInfo.WorkingDirectory = workingDirectory;
				process.StartInfo.RedirectStandardOutput = true;
				process.EnableRaisingEvents = true;

				DataReceivedEventHandler dataReceived = (_, e) =>
				{
					if (e.Data != null)
						result.Add(e.Data);
				};

				process.OutputDataReceived += dataReceived;
//				Console.WriteLine($" => p4 {process.StartInfo.Arguments}");
				process.Start();
				process.BeginOutputReadLine();
				process.WaitForExit();
				process.Close();

				return result;
			}
		}
	}

	public enum PSP4FileSyntax
	{
		Local,
		LocalRelative,
		Client,
		Depot
	}

	public class PSP4ClientInfo
	{
		public string UserName { get; set; } = string.Empty;

		public string ClientName { get; set; } = string.Empty;

		public string ClientHost { get; set; } = string.Empty;

		public string ClientRoot { get; set; } = string.Empty;

		public string ClientStream { get; set; } = string.Empty;

		public string ClientAddress { get; set; } = string.Empty;

		public string ServerAddress { get; set; } = string.Empty;
	}

	public class PSP4ChangeListItem
	{
		public int ChangeList { get; set; }

		public DateTime DateTime { get; set; }

		public string UserName { get; set; }

		public string ClientName { get; set; }

		public string Status { get; set; }

		public string Description { get; set; }
	}

	public class PSP4FileLogItem
	{
		public int Revision { get; set; }

		public int ChangeList { get; set; }

		public string Action { get; set; }
	
		public DateTime DateTime { get; set; }

		public string UserName { get; set; }

		public string ClientName { get; set; }

		public string Description { get; set; }
	}

	public class PSP4OpenedFile
	{
		public string FilePath { get; set; }
		
		public int Revision { get; set; }

		public int ChangeList { get; set; }

		public string Action { get; set; }
	}

	public class PSP4SessionState
	{
		public PSP4ClientInfo ClientInfo { get; set; }
	}

	public class PSP4CommandArguments
	{
		public string Command { get; set; } = string.Empty;

		public List<string> CommandArguments { get; set; } = new List<string>();

		public SessionState SessionState { get; set; }

		public PSP4FileSyntax FileSyntax { get; set; } = PSP4FileSyntax.LocalRelative;

		public override string ToString()
		{
			return $"Command: {Command}, Arguments: {string.Join(" ", CommandArguments)}";
		}
	}

	public class PSP4ExecutionResult
	{
		public PSP4CommandArguments Arguments { get; set; }

		public List<string> Output { get; set; }

		public SessionState SessionState { get { return Arguments.SessionState; } }

		public PSP4ClientInfo ClientInfo { get { return SessionState.PSP4().ClientInfo; } }
	}

	public static class PSP4Command
	{
		private static Dictionary<string, Func<PSP4CommandArguments, PSP4ExecutionResult>> handlers;

		static PSP4Command()
		{
			handlers = new Dictionary<string, Func<PSP4CommandArguments, PSP4ExecutionResult>>
			{ 
				{ "changes" , Changes }
			};
		}

		public static PSP4ExecutionResult Execute(PSP4CommandArguments arguments)
		{
			Func<PSP4CommandArguments, PSP4ExecutionResult> handler;
			if (handlers.TryGetValue(arguments.Command.ToLower(), out handler))
			{
				var result = handler(arguments);
				result.Arguments = arguments;

				return result;
			}
			else
			{
				var output = P4Helper.InvokeP4(
					arguments.SessionState.Path.CurrentFileSystemLocation.Path,
					arguments.Command,
					arguments.CommandArguments);

				return new PSP4ExecutionResult { Arguments = arguments, Output = output };
			}
		}

		private static PSP4ExecutionResult Changes(PSP4CommandArguments arguments)
		{
			if (arguments.CommandArguments.Remove("-my"))
			{
				arguments.CommandArguments.Add("-u");
				arguments.CommandArguments.Add(arguments.SessionState.PSP4().ClientInfo.UserName);
				arguments.CommandArguments.Add("-c");
				arguments.CommandArguments.Add(arguments.SessionState.PSP4().ClientInfo.ClientName);
			}

			var result = new PSP4ExecutionResult();

			result.Output = P4Helper.InvokeP4(
				arguments.SessionState.Path.CurrentFileSystemLocation.Path,
				arguments.Command,
				arguments.CommandArguments);

			return result;
		}
	}

	internal static class FileSyntaxHelper
	{
		public static string FromDepotSyntaxTo(PSP4FileSyntax syntax, string fileName, PSP4ClientInfo clientInfo)
		{
			switch (syntax)
			{
				case PSP4FileSyntax.LocalRelative:
				{
					return fileName.Remove(0, clientInfo.ClientStream.Length + 1).Replace("/", @"\");
				}
				case PSP4FileSyntax.Depot:
				{
					return fileName;
				}
				case PSP4FileSyntax.Local:
				{
					return Path.GetFullPath($"{clientInfo.ClientRoot}\\{fileName.Remove(0, clientInfo.ClientStream.Length + 1)}");
				}
				default:
				{
					return fileName;
				}
			}
		}
	}

	internal static class SessionStateEx
	{
		public static T GetOrCreateVariable<T>(this SessionState sessionState, string variableName, Func<T> createValue = null)
		{
			var variable = sessionState.PSVariable.Get(variableName);

			if (variable == null && createValue != null)
			{
				variable = new PSVariable(variableName, createValue());
				sessionState.PSVariable.Set(variable);
			}

			return (T)variable.Value;
		}
	
		public static PSP4SessionState PSP4(this SessionState sessionState)
		{
			return sessionState.GetOrCreateVariable<PSP4SessionState>("PSP4", () => new PSP4SessionState());
		}

		public static void SetVariable<T>(this SessionState sessionState, string variableName, T variableValue)
		{
			var variable = sessionState.PSVariable.Get(variableName);

			if (variable == null)
			{
				variable = new PSVariable(variableName, variableValue);
			}
			else
			{
				variable.Value = variableValue;
			}

			sessionState.PSVariable.Set(variable);
		}
	}

	internal static class StructuredOutput
	{
		private static Dictionary<string, Func<PSP4ExecutionResult, object>> handlers;

		static StructuredOutput()
		{
			handlers = new Dictionary<string, Func<PSP4ExecutionResult, object>>
			{ 
				{ "info" , Info },
				{ "changes" , Changes },
				{ "opened" , Opened },
				{ "filelog" , FileLog }
			};
		}

		private static object Info(PSP4ExecutionResult executionResult)
		{
			var arguments = executionResult.Arguments;
			if (arguments.SessionState != null && arguments.SessionState.PSP4().ClientInfo != null)
			{
				return arguments.SessionState.PSP4().ClientInfo;
			}

			var clientInfo = new PSP4ClientInfo();

			foreach (var line in executionResult.Output)
			{
				if (string.IsNullOrEmpty(line))
					continue;

				var matches = Regex.Matches(line, @"^user\sname:\s([0-9a-zA-Z_\-\.@]*)", RegexOptions.IgnoreCase);
				if (matches.Count > 0)
				{
					clientInfo.UserName = matches[0].Groups[1].Captures[0].Value;
				}

				matches = Regex.Matches(line, @"^client\sname:\s([0-9a-zA-Z_\-\.@]*)", RegexOptions.IgnoreCase);
				if (matches.Count > 0)
				{
					clientInfo.ClientName = matches[0].Groups[1].Captures[0].Value;
				}

				matches = Regex.Matches(line, @"^client\shost:\s([0-9a-zA-Z_\-\.@]*)", RegexOptions.IgnoreCase);
				if (matches.Count > 0)
				{
					clientInfo.ClientHost = matches[0].Groups[1].Captures[0].Value;
				}

				matches = Regex.Matches(line, @"^client\sroot:\s([0-9a-zA-Z_\-\.@\\\:]*)", RegexOptions.IgnoreCase);
				if (matches.Count > 0)
				{
					clientInfo.ClientRoot = matches[0].Groups[1].Captures[0].Value;
				}
	
				matches = Regex.Matches(line, @"^client\sstream:\s([0-9a-zA-Z_\-\.\/]*)", RegexOptions.IgnoreCase);
				if (matches.Count > 0)
				{
					clientInfo.ClientStream = matches[0].Groups[1].Captures[0].Value;
				}

				matches = Regex.Matches(line, @"^client\saddress:\s(.*)", RegexOptions.IgnoreCase);
				if (matches.Count > 0)
				{
					clientInfo.ClientAddress = matches[0].Groups[1].Captures[0].Value;
				}

				matches = Regex.Matches(line, @"^server\saddress:\s(.*)", RegexOptions.IgnoreCase);
				if (matches.Count > 0)
				{
					clientInfo.ServerAddress = matches[0].Groups[1].Captures[0].Value;
				}
			}

			arguments.SessionState.PSP4().ClientInfo = clientInfo;

			return clientInfo;
		}

		private static object Opened(PSP4ExecutionResult executionResult)
		{
			var clientInfo = executionResult.ClientInfo;

			return executionResult.Output
				.Select(line => Regex.Matches(line, @"(^\/\/[0-9a-zA-Z\/\-_\.]*)#(\d+)\s-\s(edit|add)\s*(default)?\schange\s(\d+)?", RegexOptions.IgnoreCase))
				.Where(matches => matches.Count > 0)
				.Select(matches =>
				{
					var openedFile = new PSP4OpenedFile();

					var depotFilePath = matches[0].Groups[1].Captures[0].Value;
					openedFile.FilePath = FileSyntaxHelper.FromDepotSyntaxTo(executionResult.Arguments.FileSyntax, depotFilePath, clientInfo); 
					openedFile.Revision = int.Parse(matches[0].Groups[2].Captures[0].Value);
					openedFile.Action = matches[0].Groups[3].Captures[0].Value;
					if (matches[0].Groups[4].Captures.Count > 0)
					{
						openedFile.ChangeList = -1; //Default
					}
					else
					{
						openedFile.ChangeList = int.Parse(matches[0].Groups[5].Captures[0].Value);
					}

					return openedFile;
				})
				.ToList();
		}

		private static object Changes(PSP4ExecutionResult executionResult)
		{
			var arguments = executionResult.Arguments;
			var changes = new List<PSP4ChangeListItem>();

			if (arguments.CommandArguments.Any(arg => arg.ToLower() == "-l" || arg.ToLower() == "-L"))
			{
				PSP4ChangeListItem currentChange = null;
				var sb = new StringBuilder();

				foreach (var line in executionResult.Output)
				{
					var matches = Regex.Matches(line, @"^(\w+)\s(\d+)\son\s(\d+\/\d+\/\d+)\sby\s([0-9a-zA-Z-\._]+)@([(0-9a-zA-Z-\._]+)", RegexOptions.IgnoreCase);
					
					if (matches.Count > 0)
					{
						if (currentChange != null)
						{
							currentChange.Description = sb.ToString();
							sb.Clear();
						}

						currentChange = new PSP4ChangeListItem();
						changes.Add(currentChange);

						currentChange.ChangeList = int.Parse(matches[0].Groups[2].Captures[0].Value);
						DateTime dateTime;
						if (DateTime.TryParse(matches[0].Groups[3].Captures[0].Value, out dateTime))
							currentChange.DateTime = dateTime;
						currentChange.UserName = matches[0].Groups[4].Captures[0].Value;
						currentChange.ClientName = matches[0].Groups[5].Captures[0].Value;
					}
					else if (currentChange != null)
					{
						sb.AppendLine(line);
					}
				}

				if (currentChange != null)
				{
					currentChange.Description = sb.ToString();
					sb.Clear();
				}

				return changes;
			}
			else
			{
				foreach (var line in executionResult.Output)
				{
					if (string.IsNullOrEmpty(line))
						continue;

					var matches = Regex.Matches(line, @"^(\w+)\s(\d+)\son\s(\d+\/\d+\/\d+)\sby\s([0-9a-zA-Z-\._]+)@([(0-9a-zA-Z-\._]+)\s(\*pending\*)?\s?(.*)", RegexOptions.IgnoreCase);
					if (matches.Count == 0)
						continue;

					var change = new PSP4ChangeListItem();

					change.ChangeList = int.Parse(matches[0].Groups[2].Captures[0].Value);
					DateTime dateTime;
					if (DateTime.TryParse(matches[0].Groups[3].Captures[0].Value, out dateTime))
						change.DateTime = dateTime;
					change.UserName = matches[0].Groups[4].Captures[0].Value;
					change.ClientName = matches[0].Groups[5].Captures[0].Value;
					change.Status = matches[0].Groups[6].Captures.Count > 0 ? matches[0].Groups[6].Captures[0].Value : string.Empty;
					change.Description = matches[0].Groups[7].Captures[0].Value;

					changes.Add(change);
				}
			}

			return changes;
		}

		private static object FileLog(PSP4ExecutionResult executionResult)
		{
			var arguments = executionResult.Arguments;

			if (arguments.CommandArguments.Any(arg => arg.ToLower() == "-l" || arg.ToLower() == "-L"))
			{
				return executionResult.Output;
			}
			else
			{
				var filelog = executionResult.Output
					.Select(line => Regex.Matches(line, @".*#(\d+)\schange\s(\d+)\s(\w+)\son\s(\d+\/\d+\/\d+)\sby\s([0-9a-zA-Z-\._]+)@([0-9a-zA-Z-\._]+)\s\((\w+)\)\s(.*)", RegexOptions.IgnoreCase))
					.Where(matches => matches.Count > 0)
					.Select(matches => matches[0])
					.Select(match =>
					{
						var logItem = new PSP4FileLogItem();

						logItem.Revision = int.Parse(match.Groups[1].Captures[0].Value);
						logItem .ChangeList = int.Parse(match.Groups[2].Captures[0].Value);
						logItem .Action = match.Groups[3].Captures[0].Value;

						DateTime dateTime;
						if (DateTime.TryParse(match.Groups[4].Captures[0].Value, out dateTime))
							logItem .DateTime = dateTime;

						logItem.UserName = match.Groups[5].Captures[0].Value;
						logItem.ClientName = match.Groups[6].Captures[0].Value;
						logItem.Description = match.Groups[8].Captures[0].Value;

						return logItem;
					})
					.ToList();

				return filelog;
			}
		}

		public static object Parse(PSP4ExecutionResult executionResult)
		{
			Func<PSP4ExecutionResult, object> handler;
			if (handlers.TryGetValue(executionResult.Arguments.Command.ToLower(), out handler))
			{
				return handler(executionResult);
			}

			return executionResult.Output;
		}
	}

    [Cmdlet(VerbsLifecycle.Invoke,"PSP4")]
	[Alias("pp4")]
    [OutputType(typeof(object))]
    public class InvokePSP4Cmdlet : PSCmdlet
	{
        [Parameter(Position = 0, Mandatory = true, ValueFromPipelineByPropertyName = true)]
        public string Command { get; set; } = "info";

        [Parameter(Position = 1, Mandatory = false, ValueFromPipelineByPropertyName = true, ValueFromRemainingArguments = true)]
        public string[] Arguments { get; set; }

        protected override void ProcessRecord()
		{
			bool resetWorkspace = Command.ToLower() == "reset";
			var clientInfo = InitializeSession(resetWorkspace);

			if (resetWorkspace)
			{
				WriteObject(clientInfo);
			}
			else
			{
				var arguments = (Arguments ?? new string[] {}).ToList();

				//TODO: Find a way to use switch parameters. Currently
				// they interfer with regular p4 flags, i.e -l will interfer
				// with a switch parameter -LocalSyntax
				var fileSyntax = PSP4FileSyntax.LocalRelative;
				if (arguments.Remove("-localsyntax"))
					fileSyntax = PSP4FileSyntax.Local;
				if (arguments.Remove("-depotsyntax"))
					fileSyntax = PSP4FileSyntax.Depot;

				var result = PSP4Command.Execute(new PSP4CommandArguments
				{
					Command = Command,
					CommandArguments = arguments,
					SessionState = SessionState,
					FileSyntax = fileSyntax
				});

				var structuredOutput = StructuredOutput.Parse(result);
				WriteObject(structuredOutput, true);
			}
		}

		private PSP4ClientInfo InitializeSession(bool reset)
		{
			var state = SessionState.PSP4();
			var currentPath = SessionState.Path.CurrentFileSystemLocation.Path;

			if (state.ClientInfo == null || reset)
			{
				var arguments = new PSP4CommandArguments
				{
					Command = "info",
					CommandArguments = new List<string>(),
					SessionState = SessionState
				};

				state.ClientInfo = null;
				var result = PSP4Command.Execute(arguments);
				state.ClientInfo = StructuredOutput.Parse(result) as PSP4ClientInfo;

				SessionState.SetVariable("PSP4ClientName", state.ClientInfo.ClientName);
			}

			return state.ClientInfo;
		}
	}
}
