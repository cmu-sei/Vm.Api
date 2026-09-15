// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CSharp.RuntimeBinder;
using NSubstitute;
using Player.Vm.Api.Domain.Models;
using Player.Vm.Api.Domain.Proxmox.Services;
using Player.Vm.Api.Tests.Infrastructure;
using Xunit;

namespace Player.Vm.Api.Tests;

/// <summary>
/// The QEMU guest agent half of <c>ProxmoxService</c> - running a process in a guest, reading a file out
/// of one and writing a file into one - driven through a substituted transport so no Proxmox cluster is
/// involved. See <see cref="FakeProxmoxCluster"/> for why the seam is the socket rather than the client.
/// </summary>
/// <remarks>
/// <para>
/// The centrepiece is the shell tokenizer. <c>BuildAgentCommand</c> and <c>TokenizeShellArguments</c> are
/// <c>private static</c>, so the only way to observe what they produced is the JSON array in the body of
/// the <c>agent/exec</c> request - which makes the wire not an incidental detail of these tests but the
/// single place the parser's output exists. That is also the right place to assert it: what the tokenizer
/// is for is deciding what the guest actually runs, and an argument that lands in the wrong slot of that
/// array runs a different command.
/// </para>
/// <para>
/// The other subject here is how the responses are read, and it is where the bugs are. The service takes
/// <c>data.exited</c>, <c>data.exitcode</c> and <c>data.pid</c> off a <c>dynamic</c> and guards each with
/// <c>!= null</c>, but the value behind it is an <see cref="System.Dynamic.ExpandoObject"/>, on which
/// member access to an absent key throws <see cref="RuntimeBinderException"/> rather than answering null -
/// so none of those three guards can do what it was written to do. The one output path that is null-safe
/// is <c>DecodeAgentOutput</c>, and only because the hyphenated <c>out-data</c> / <c>err-data</c> keys are
/// unreachable through dynamic member access at all and forced it through the dictionary view instead.
/// Four tests characterize that; each says what will happen to it when the bug is fixed.
/// </para>
/// <para>
/// <c>ProxmoxEndpointTests</c> covers the routes above these methods with <c>IProxmoxService</c>
/// substituted, including the guest-process timeout default and the guest file path coming off the route.
/// This class is the driver underneath, and restates none of it.
/// </para>
/// </remarks>
public class ProxmoxServiceGuestAgentTests
{
    private const int Vmid = 100;
    private const string Bash = "/bin/bash";
    private const string Exec = "/agent/exec";
    private const string ExecStatus = "/agent/exec-status";
    private const string FileRead = "/agent/file-read";
    private const string FileWrite = "/agent/file-write";

    #region The shell tokenizer

    // The program takes slot 0 and each argument is its own element after it, which is the shape QGA's
    // exec takes: the array is passed to execve rather than to a shell, so a token boundary in the wrong
    // place is not a formatting difference but a different program invocation.
    [Fact]
    public async Task RunGuestProcess_PutsTheProgramInSlotZeroAndEachArgumentAfterIt()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");

        await cluster.Service().RunGuestProcessFast(info, Bash, "-c hello");

        Assert.Equal(
            [Bash, "-c", "hello"],
            Command(cluster.Request(HttpMethod.Post, FakeProxmoxCluster.VmPath(info, Exec))));
    }

    // Nothing to tokenize is the program alone rather than a program with an empty argument. Note the
    // guard is IsNullOrEmpty, not IsNullOrWhiteSpace, so an all-whitespace string does go through the
    // tokenizer - and comes out with no tokens, which is the same answer by a different route.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public async Task RunGuestProcess_WithNoArguments_SendsTheProgramAlone(string arguments)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");

        await cluster.Service().RunGuestProcessFast(info, Bash, arguments);

        Assert.Equal(
            [Bash],
            Command(cluster.Request(HttpMethod.Post, FakeProxmoxCluster.VmPath(info, Exec))));
    }

    // The whole of the quote and escape grammar, one case per row, asserted as the tokens that reached the
    // wire after the program. This is the richest logic in the driver and the least visible: a caller
    // writes one string in a view's task definition and what the guest runs is whatever this produces.
    //
    // The two escape rules are the ones worth reading the rows for. Inside double quotes only \, ", $,
    // backtick and newline are escapable and every other backslash is kept literally, so "a\\b" collapses
    // to a\b while "a\nb" keeps its backslash. Inside single quotes nothing is escapable at all, which
    // includes the closing quote. Outside quotes a backslash escapes whatever follows it, which is how a
    // space or a quote character gets into a token without quoting.
    [Theory]
    // Whitespace separates tokens; runs of it collapse and leading and trailing runs produce no empty
    // tokens. Tabs and newlines count, since the test is char.IsWhiteSpace.
    [InlineData("-c hello", new[] { "-c", "hello" })]
    [InlineData("   -c    hello   ", new[] { "-c", "hello" })]
    [InlineData("-c\thello\nworld", new[] { "-c", "hello", "world" })]
    // Both quote characters group whitespace into one token.
    [InlineData("-c \"touch /tmp/x.txt\"", new[] { "-c", "touch /tmp/x.txt" })]
    [InlineData("-c 'echo hi'", new[] { "-c", "echo hi" })]
    // Inside double quotes: the five escapable characters.
    [InlineData("-c \"say \\\"hi\\\"\"", new[] { "-c", "say \"hi\"" })]
    [InlineData("-c \"a\\\\b\"", new[] { "-c", "a\\b" })]
    [InlineData("-c \"a\\$b\"", new[] { "-c", "a$b" })]
    [InlineData("-c \"a\\`b\"", new[] { "-c", "a`b" })]
    // Inside double quotes: anything else keeps its backslash. A caller who writes "\n" meaning a newline
    // gets a backslash and an n, which the guest's own shell may or may not go on to interpret.
    [InlineData("-c \"a\\nb\"", new[] { "-c", "a\\nb" })]
    [InlineData("-c \"a\\tb\"", new[] { "-c", "a\\tb" })]
    // Inside single quotes: nothing is escapable, so a backslash is a character and cannot protect the
    // closing quote - 'a\' is the token a\ and the quote still closes.
    [InlineData("-c 'a\\nb'", new[] { "-c", "a\\nb" })]
    [InlineData("-c 'a\\'", new[] { "-c", "a\\" })]
    // Outside quotes: a backslash escapes the next character, whatever it is, which is the only way to get
    // a space or a bare quote into an unquoted token.
    [InlineData("-c a\\ b", new[] { "-c", "a b" })]
    [InlineData("-c a\\\"b", new[] { "-c", "a\"b" })]
    // A backslash with nothing after it fails the i + 1 < length guard and is kept as a character.
    [InlineData("-c ab\\", new[] { "-c", "ab\\" })]
    // An empty quoted string is an empty token rather than no token: hasToken is set on seeing the quote,
    // so a caller can pass an argument that is deliberately blank.
    [InlineData("-c \"\"", new[] { "-c", "" })]
    [InlineData("-c ''", new[] { "-c", "" })]
    // Quoted and unquoted runs with no whitespace between them are one token, not several.
    [InlineData("a\"b\"c", new[] { "abc" })]
    [InlineData("'a'b\"c\"", new[] { "abc" })]
    [InlineData("-c \"a b\"c", new[] { "-c", "a bc" })]
    public async Task RunGuestProcess_TokenizesItsArgumentsTheWayAPosixShellWould(
        string arguments, string[] expected)
    {
        Assert.Equal(expected, await Tokens(arguments));
    }

    // A quote that is never closed throws rather than being tolerated, and throws before anything is sent.
    // The doc comment on TokenizeShellArguments is explicit about the trade: a mismatched quote silently
    // absorbed would run a command nobody wrote - the tail of the arguments folded into one token - so
    // failing loudly is the safer answer even though it turns a typo into a 500.
    [Theory]
    [InlineData("-c \"oops")]
    [InlineData("-c 'oops")]
    [InlineData("-c \"a'b")]
    [InlineData("-c 'a\"b")]
    public async Task RunGuestProcess_WithAnUnterminatedQuote_ThrowsBeforeSendingAnything(string arguments)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);

        var run = await Assert.ThrowsAsync<ArgumentException>(
            () => cluster.Service().RunGuestProcess(info, Bash, arguments, TimeSpan.FromSeconds(5)));
        var fast = await Assert.ThrowsAsync<ArgumentException>(
            () => cluster.Service().RunGuestProcessFast(info, Bash, arguments));

        Assert.Equal("Unterminated quote in arguments string.", run.Message);
        Assert.Equal("Unterminated quote in arguments string.", fast.Message);
        Assert.Empty(cluster.Http.Sent);
    }

    #endregion

    #region RunGuestProcess

    // The two-request shape of a guest process: one POST to exec carrying the command array, then a GET to
    // exec-status carrying the pid the exec answered with. Neither is addressed through ResolveNode, so the
    // whole exchange is two requests and no cluster read.
    [Fact]
    public async Task RunGuestProcess_ExecsAndThenReadsTheStatusOfThePidItWasGiven()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Answers(
            $"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}",
            """{"exited":1,"exitcode":0,"out-data":"hello\n","err-data":""}""");

        var result = await cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5));

        Assert.Equal(
            [FakeProxmoxCluster.VmPath(info, Exec), FakeProxmoxCluster.VmPath(info, ExecStatus)],
            cluster.Http.Paths);
        Assert.Equal("?pid=4242", cluster.Request(HttpMethod.Get, FakeProxmoxCluster.VmPath(info, ExecStatus)).Query);
        Assert.Equal("hello\n", result.Output);
    }

    // The loop really loops: a guest that reports "not exited yet" once is asked again rather than being
    // reported as finished with an empty result. GuestProcessPollMs is zero in the harness, so the spin
    // costs nothing; a deployment pays the shipped 500ms per pass.
    [Fact]
    public async Task RunGuestProcess_KeepsPollingUntilTheGuestReportsTheProcessExited()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Http.AnswersJson(
            $"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}",
            FakeProxmoxCluster.Data("""{"exited":0}"""),
            HttpStatusCode.OK,
            once: true);
        cluster.Answers(
            $"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}",
            """{"exited":1,"exitcode":0,"out-data":"done","err-data":""}""");

        var result = await cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(30));

        Assert.Equal(2, cluster.Requests(HttpMethod.Get, FakeProxmoxCluster.VmPath(info, ExecStatus)).Count);
        Assert.Equal("done", result.Output);
    }

    // What the caller is told when the process finished: the guest's own exit code, success only when that
    // code is zero, and the two output streams. Success is the field the callers branch on, so the whole
    // difference between a script that worked and one that failed is the == 0.
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(127, false)]
    [InlineData(-1, false)]
    public async Task RunGuestProcess_ReportsTheGuestsExitCodeAndCallsOnlyZeroASuccess(int exitCode, bool success)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Answers(
            $"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}",
            $"{{\"exited\":1,\"exitcode\":{exitCode},\"out-data\":\"out\",\"err-data\":\"\"}}");

        var result = await cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5));

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(success, result.Success);
        Assert.Equal("out", result.Output);
        Assert.Empty(result.Error);
    }

    // The odd half of the output contract, and the reason a caller cannot print Output and Error together:
    // when the guest wrote anything to stderr, Output is stdout *concatenated with* stderr, and Error then
    // repeats what is already at the end of Output. There is no separator either, so a command whose stdout
    // does not end in a newline runs its two streams into one line.
    [Fact]
    public async Task RunGuestProcess_WhenTheGuestWroteToStderr_AppendsItToOutputAsWellAsReportingIt()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Answers(
            $"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}",
            """{"exited":1,"exitcode":1,"out-data":"partial","err-data":"boom"}""");

        var result = await cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5));

        Assert.Equal("partialboom", result.Output);
        Assert.Equal("boom", result.Error);
    }

    // The timeout answer, which is a result rather than an exception - the callers treat a guest process as
    // something that can fail without the request failing. The deadline is checked *after* the status read,
    // so a zero timeout still costs exactly one poll and a process that has already finished is still
    // reported rather than timed out (the test below).
    [Fact]
    public async Task RunGuestProcess_WhenTheGuestNeverExits_TimesOutWithMinusOneAfterOnePoll()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}", """{"exited":0}""");

        var result = await cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.Zero);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.Success);
        Assert.Empty(result.Output);
        Assert.Equal("QGA exec timed out after 0s (pid=4242)", result.Error);
        Assert.Single(cluster.Requests(HttpMethod.Get, FakeProxmoxCluster.VmPath(info, ExecStatus)));
    }

    // The other side of the same ordering: the exited check precedes the deadline check, so a process that
    // finished is reported on its own terms even when the timeout has already expired. Which matters
    // because the timeout in a deployment is a per-command budget, not a promise about the poll schedule.
    [Fact]
    public async Task RunGuestProcess_WithAnExpiredTimeoutButAFinishedProcess_ReportsTheResultNotATimeout()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Answers(
            $"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}",
            """{"exited":1,"exitcode":0,"out-data":"in time","err-data":""}""");

        var result = await cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.Zero);

        Assert.True(result.Success);
        Assert.Equal("in time", result.Output);
    }

    // The timeout the message reports is the one the caller asked for, rendered with no decimals, and it is
    // not clamped - a negative budget is a deadline already past, which times out on the first poll and
    // says so verbatim. Only a timeout that has already expired can be asserted without spending it: with a
    // positive one the loop really waits, and GuestProcessPollMs is zero here.
    [Theory]
    [InlineData(0, "0s")]
    [InlineData(-90, "-90s")]
    public async Task RunGuestProcess_ReportsTheTimeoutItWasGivenVerbatimAndWholeSeconds(
        int seconds, string reported)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}", """{"exited":0}""");

        var result = await cluster.Service().RunGuestProcess(
            info, Bash, "-c hello", TimeSpan.FromSeconds(seconds));

        Assert.Equal($"QGA exec timed out after {reported} (pid=4242)", result.Error);
    }

    // A refused exec and a refused exec-status are separate messages, and both name the vmid; the second
    // names the pid as well, which is the only handle an operator has on a process already started in a
    // guest. Nothing retries either - unlike the bulk power path, a guest command is not re-resolved and
    // re-sent when the node turns out to be wrong.
    //
    // The context is what makes these usable, and it is worth the contrast with GetConsole, which throws
    // result.GetError() bare: GetError() is populated only from an errors object in the body, so a plain
    // 401 or a proxy's 502 produces an exception with an empty message there. Here the same empty string
    // still arrives inside "QGA exec failed for vmid=100: ", which names the operation and the machine
    // whatever Proxmox did or did not say. Asserted with StartsWith for that reason rather than on the
    // whole message.
    [Fact]
    public async Task RunGuestProcess_WhenProxmoxRefusesTheExec_ThrowsNamingTheVmid()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Rejects(
            $"POST {FakeProxmoxCluster.VmPath(info, Exec)}", "No QEMU guest agent configured");

        var ex = await Assert.ThrowsAsync<Exception>(
            () => cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5)));

        Assert.StartsWith($"QGA exec failed for vmid={Vmid}: ", ex.Message);
        Assert.Contains("No QEMU guest agent configured", ex.Message);
        Assert.Equal([FakeProxmoxCluster.VmPath(info, Exec)], cluster.Http.Paths);
    }

    [Fact]
    public async Task RunGuestProcess_WhenProxmoxRefusesTheStatusRead_ThrowsNamingTheVmidAndThePid()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Rejects($"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}", "no such process");

        var ex = await Assert.ThrowsAsync<Exception>(
            () => cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5)));

        Assert.StartsWith($"QGA exec-status failed for vmid={Vmid} pid=4242: ", ex.Message);
        Assert.Contains("no such process", ex.Message);
    }

    // Neither request goes through ResolveNode, so a machine that has migrated is addressed on the node the
    // caller still believes it is on and nothing corrects it. GetConsole, ChangeNetwork and MountIso all
    // resolve first; these four do not, which makes a guest command the one Proxmox operation that fails
    // outright after a migration until the state poller has caught up. Asserted by the paths, and by the
    // cluster resource list never being read at all.
    [Fact]
    public async Task RunGuestProcess_AfterAMigration_StillAddressesTheNodeTheCallerHolds()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, node: "pve1");
        cluster.Migrates(Vmid, "pve2");
        cluster.Answers($"POST api2/json/nodes/pve1/qemu/{Vmid}{Exec}", """{"pid":4242}""");
        cluster.Answers(
            $"GET api2/json/nodes/pve1/qemu/{Vmid}{ExecStatus}",
            """{"exited":1,"exitcode":0,"out-data":"","err-data":""}""");

        await cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5));

        Assert.Equal(
            [$"api2/json/nodes/pve1/qemu/{Vmid}{Exec}", $"api2/json/nodes/pve1/qemu/{Vmid}{ExecStatus}"],
            cluster.Http.Paths);
        Assert.DoesNotContain(FakeProxmoxCluster.ClusterResources, cluster.Http.Paths);
        Assert.Equal("pve1", info.Node);
    }

    // A guest command changes nothing Proxmox reports about the machine, so unlike every power operation it
    // does not nudge the state poller. Pinned because adding a CheckState here would put a cluster-wide
    // resource read behind every line of a task script.
    [Fact]
    public async Task RunGuestProcess_DoesNotWakeTheStatePoller()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Answers(
            $"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}",
            """{"exited":1,"exitcode":0,"out-data":"","err-data":""}""");

        await cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5));

        cluster.State.DidNotReceive().CheckState();
    }

    #endregion

    #region Reading the exec-status response

    /// <summary>
    /// A REAL BUG, characterized rather than fixed. When this is fixed these two rows go red, and the
    /// expected answers are: an absent <c>exited</c> is "not exited yet" and should poll again, and an
    /// absent <c>exitcode</c> should produce <c>ExitCode = -1</c> as the code at line 543 intends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>RunGuestProcess</c> writes <c>data.exited != null ? (int)data.exited : 0</c> and
    /// <c>data.exitcode != null ? (int)data.exitcode : -1</c>. Both fallbacks are dead: <c>data</c> is an
    /// <see cref="System.Dynamic.ExpandoObject"/>, and member access to a key it does not hold throws
    /// <see cref="RuntimeBinderException"/> - <c>'System.Dynamic.ExpandoObject' does not contain a
    /// definition for 'exited'</c> - instead of evaluating to null. The <c>!= null</c> guard is reached
    /// only when the key is present and explicitly JSON <c>null</c>.
    /// </para>
    /// <para>
    /// That matters because omitting the key is what QGA actually does. PVE's <c>agent/exec-status</c>
    /// documents <c>exited</c> and <c>exitcode</c> as optional, and a process that is still running is
    /// reported without an <c>exitcode</c> - some agent versions without <c>exited</c> either. So the
    /// polling loop this class asserts above is only reachable because the guest answered
    /// <c>"exited":0</c> explicitly; against an agent that omits the key instead, the first poll throws out
    /// of <c>RunGuestProcess</c> and the caller sees an unhandled <c>RuntimeBinderException</c> naming an
    /// ExpandoObject rather than a running process or a timeout.
    /// </para>
    /// <para>
    /// The fix is the dictionary view <c>DecodeAgentOutput</c> already uses for the hyphenated keys -
    /// <c>data is IDictionary&lt;string, object&gt; d &amp;&amp; d.TryGetValue("exited", out var v)</c> -
    /// which is null-safe by construction and would make both fallbacks live.
    /// </para>
    /// <para>
    /// That the two fallbacks are dead rather than merely untested was established by mutation: changing
    /// the <c>: 0</c> behind <c>exited</c> to <c>: 1</c> and the <c>: -1</c> behind <c>exitcode</c> to
    /// <c>: -99</c> together reddens nothing in this class. Normally a mutation that fails no test is a
    /// coverage hole; here it is the point, because no arrangement can reach either value - the ternary
    /// throws while evaluating its own condition.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("""{"exitcode":0,"out-data":"","err-data":""}""", "exited")]
    [InlineData("""{"exited":1,"out-data":"","err-data":""}""", "exitcode")]
    public async Task RunGuestProcess_WhenTheStatusOmitsAKeyItGuardsForNull_ThrowsFromTheDynamicBinder(
        string status, string missing)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}", status);

        var ex = await Assert.ThrowsAsync<RuntimeBinderException>(
            () => cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5)));

        Assert.Equal(
            $"'System.Dynamic.ExpandoObject' does not contain a definition for '{missing}'",
            ex.Message);
    }

    /// <summary>
    /// The same bug on the exec response. An <c>agent/exec</c> answer with no <c>pid</c> throws from the
    /// binder rather than being reported as a failed exec, in both methods that call it.
    /// </summary>
    /// <remarks>
    /// Narrower than the exec-status case - PVE's exec always answers a pid when it answers at all - but it
    /// is the same unguarded dereference, and it means a 200 with an unexpected body surfaces as an
    /// ExpandoObject binder error rather than as <c>QGA exec failed for vmid=...</c>. Goes red with the same
    /// fix; the expected answer after it is the exec-failed exception.
    /// </remarks>
    [Fact]
    public async Task RunGuestProcess_WhenTheExecAnswersNoPid_ThrowsFromTheDynamicBinder()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{}""");

        var run = await Assert.ThrowsAsync<RuntimeBinderException>(
            () => cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5)));
        var fast = await Assert.ThrowsAsync<RuntimeBinderException>(
            () => cluster.Service().RunGuestProcessFast(info, Bash, "-c hello"));

        Assert.Equal("'System.Dynamic.ExpandoObject' does not contain a definition for 'pid'", run.Message);
        Assert.Equal("'System.Dynamic.ExpandoObject' does not contain a definition for 'pid'", fast.Message);
    }

    // The one response path that is null-safe, and only by accident of the key names. QGA spells the two
    // output streams "out-data" and "err-data", which dynamic member access cannot reach at all - a hyphen
    // is not an identifier - so DecodeAgentOutput goes through the ExpandoObject's IDictionary view, which
    // answers "absent" instead of throwing. Both keys are read, and an absent one is the empty string
    // rather than a null or a binder exception. Contrast the two tests above, where the guard is written to
    // look defensive and is not.
    [Fact]
    public async Task RunGuestProcess_ReadsBothHyphenatedOutputKeysThroughTheDictionaryView()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Answers(
            $"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}",
            """{"exited":1,"exitcode":2,"out-data":"stdout here","err-data":"stderr here"}""");

        var result = await cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5));

        Assert.Equal("stdout herestderr here", result.Output);
        Assert.Equal("stderr here", result.Error);
    }

    // A command that wrote nothing at all: both keys absent, and the answer is two empty strings rather
    // than the binder exception the sibling keys produce. This is the case a successful "touch" or
    // "systemctl restart" produces on a real guest, so it is also the common one.
    [Fact]
    public async Task RunGuestProcess_WhenTheStatusOmitsBothOutputKeys_ReportsEmptyStringsRatherThanThrowing()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}", """{"exited":1,"exitcode":0}""");

        var result = await cluster.Service().RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5));

        Assert.Equal(string.Empty, result.Output);
        Assert.Equal(string.Empty, result.Error);
        Assert.True(result.Success);
    }

    #endregion

    #region RunGuestProcessFast

    // Fire and forget: the exec and nothing else, with the pid handed back so a caller that wants the
    // result can read exec-status itself. One request is the whole of the contract - the difference from
    // RunGuestProcess is not a shorter timeout but no polling at all.
    [Fact]
    public async Task RunGuestProcessFast_ExecsOnceAndReportsThePidWithoutPolling()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");

        Assert.Equal(4242L, await cluster.Service().RunGuestProcessFast(info, Bash, "-c \"echo hi\""));

        Assert.Equal([FakeProxmoxCluster.VmPath(info, Exec)], cluster.Http.Paths);
        Assert.Equal(
            """{"command":["/bin/bash","-c","echo hi"]}""",
            cluster.Request(HttpMethod.Post, FakeProxmoxCluster.VmPath(info, Exec)).Body);
    }

    [Fact]
    public async Task RunGuestProcessFast_WhenProxmoxRefusesTheExec_ThrowsNamingTheVmid()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Rejects($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", "agent is not running");

        var ex = await Assert.ThrowsAsync<Exception>(
            () => cluster.Service().RunGuestProcessFast(info, Bash, "-c hello"));

        Assert.StartsWith($"QGA exec failed for vmid={Vmid}: ", ex.Message);
        Assert.Contains("agent is not running", ex.Message);
    }

    /// <summary>
    /// The pid is a <c>long</c> here and is narrowed to <c>int</c> by <c>RunGuestProcess</c> for the
    /// exec-status query, so a pid above <see cref="int.MaxValue"/> is silently truncated and the poll
    /// watches a different process.
    /// </summary>
    /// <remarks>
    /// Unreachable on Linux, where <c>kernel.pid_max</c> tops out at 2^22, which is why the narrowing has
    /// never bitten; it is characterized because the two methods disagree about the type of the same value
    /// and only one of them is right. A fix that widens the query would turn the second assertion here red.
    /// </remarks>
    [Fact]
    public async Task RunGuestProcess_WithAPidAboveIntRange_ReportsItWholeButPollsATruncatedOne()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4294967303}""");
        cluster.Answers(
            $"GET {FakeProxmoxCluster.VmPath(info, ExecStatus)}",
            """{"exited":1,"exitcode":0,"out-data":"","err-data":""}""");

        Assert.Equal(4294967303L, await cluster.Service().RunGuestProcessFast(info, Bash, null));

        await cluster.Service().RunGuestProcess(info, Bash, null, TimeSpan.FromSeconds(5));

        Assert.Equal("?pid=7", cluster.Request(HttpMethod.Get, FakeProxmoxCluster.VmPath(info, ExecStatus)).Query);
    }

    #endregion

    #region ReadGuestFile

    // A GET with the path in the query rather than in the route, which is what makes the encoding part of
    // the contract: PveClient puts GET parameters through HttpUtility.UrlEncode, so the separators come out
    // as lowercase %2f and a space as +. A guest path is caller-supplied - it comes off the route in
    // ProxmoxController - so both characters are reachable.
    [Fact]
    public async Task ReadGuestFile_AsksForTheFileByQueryWithTheParametersUrlEncoded()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, FileRead)}", """{"content":"file text"}""");

        Assert.Equal("file text", await cluster.Service().ReadGuestFile(info, "/tmp/my file.txt"));

        Assert.Equal([FakeProxmoxCluster.VmPath(info, FileRead)], cluster.Http.Paths);
        Assert.Equal(
            "?file=%2ftmp%2fmy+file.txt",
            cluster.Request(HttpMethod.Get, FakeProxmoxCluster.VmPath(info, FileRead)).Query);
    }

    // An empty file is an empty string, not a null: the callers concatenate what comes back, so a null here
    // would be an argument exception somewhere further up rather than "the file was empty".
    [Fact]
    public async Task ReadGuestFile_ForAnEmptyFile_AnswersAnEmptyString()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, FileRead)}", """{"content":""}""");

        Assert.Equal(string.Empty, await cluster.Service().ReadGuestFile(info, "/tmp/f.txt"));
    }

    /// <summary>
    /// The same unguarded dynamic dereference as the exec paths, in the one method whose guard looks most
    /// like it works: <c>data.content != null ? (string)data.content : string.Empty</c> throws from the
    /// binder when <c>content</c> is absent rather than answering the empty string.
    /// </summary>
    /// <remarks>
    /// Reachable through any 200 whose <c>data</c> object does not carry the key, which is what PVE answers
    /// for a file-read the agent completed with no content to report. Goes red when the fallback is made
    /// live through the dictionary view; the expected answer then is <see cref="string.Empty"/>, exactly as
    /// the empty-file test above already gets by a different route.
    /// </remarks>
    [Fact]
    public async Task ReadGuestFile_WhenTheResponseOmitsContent_ThrowsFromTheDynamicBinderRatherThanFallingBack()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"GET {FakeProxmoxCluster.VmPath(info, FileRead)}", """{"truncated":0}""");

        var ex = await Assert.ThrowsAsync<RuntimeBinderException>(
            () => cluster.Service().ReadGuestFile(info, "/tmp/f.txt"));

        Assert.Equal("'System.Dynamic.ExpandoObject' does not contain a definition for 'content'", ex.Message);
    }

    // The failure message names the path as well as the vmid, which is what distinguishes "no agent" from
    // "no such file" in a log - PVE answers both as a 500 with an errors object.
    [Fact]
    public async Task ReadGuestFile_WhenProxmoxRefusesTheRead_ThrowsNamingTheVmidAndThePath()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Rejects($"GET {FakeProxmoxCluster.VmPath(info, FileRead)}", "open failed: No such file");

        var ex = await Assert.ThrowsAsync<Exception>(() => cluster.Service().ReadGuestFile(info, "/tmp/f.txt"));

        Assert.StartsWith($"QGA file-read failed for vmid={Vmid} path=/tmp/f.txt: ", ex.Message);
        Assert.Contains("open failed: No such file", ex.Message);
    }

    #endregion

    #region UploadFileToGuest

    // A POST carrying the content and the destination in the body, and the content goes out as text: the
    // stream is read to bytes and then UTF-8 decoded, so this is a text-file upload however it is called.
    // Anything that is not valid UTF-8 is replacement-charactered on the way through rather than refused.
    [Fact]
    public async Task UploadFileToGuest_PostsTheDecodedTextAndTheDestinationPath()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Accepts($"POST {FakeProxmoxCluster.VmPath(info, FileWrite)}");

        var reported = await cluster.Service().UploadFileToGuest(
            info, "/tmp/f.txt", new MemoryStream(Encoding.UTF8.GetBytes("line one\nline two\n")));

        Assert.Equal($"wrote 18 bytes to /tmp/f.txt on vmid={Vmid}", reported);
        Assert.Equal([FakeProxmoxCluster.VmPath(info, FileWrite)], cluster.Http.Paths);
        Assert.Equal(
            """{"content":"line one\nline two\n","file":"/tmp/f.txt"}""",
            cluster.Request(HttpMethod.Post, FakeProxmoxCluster.VmPath(info, FileWrite)).Body);
    }

    // The size ceiling exists because QGA's file-write is capped around 64 KiB and a payload over it is
    // rejected by the agent with nothing useful to say, so this refuses first and says what to do instead.
    // Both sides of the boundary are driven: the check is a strict >, so exactly the limit is allowed.
    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public async Task UploadFileToGuest_AllowsExactlyTheLimitAndRefusesOneByteMore(int size, bool allowed)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Options.FileUploadMaxBytes = 8;
        cluster.Accepts($"POST {FakeProxmoxCluster.VmPath(info, FileWrite)}");
        var content = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', size)));

        if (allowed)
        {
            Assert.Equal(
                $"wrote 8 bytes to /tmp/f.txt on vmid={Vmid}",
                await cluster.Service().UploadFileToGuest(info, "/tmp/f.txt", content));

            return;
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cluster.Service().UploadFileToGuest(info, "/tmp/f.txt", content));

        // The count, the limit and the way out, because the caller is a user pasting a file into a task
        // definition and "too big" on its own leaves them nothing to do about it.
        Assert.Equal(
            "QGA file-write payload 9 bytes exceeds 8-byte limit. " +
            "Use vSphere upload or chunk uploads via guest exec.",
            ex.Message);

        // Refused before anything is sent, so an oversized payload never reaches the cluster.
        Assert.Empty(cluster.Http.Sent);
    }

    // The limit counts bytes off the stream, not characters, so a file of accented text hits it sooner than
    // its length suggests - five characters can be six bytes. The shipped limit is 60 KiB against QGA's
    // ~64 KiB, which is the headroom this makes use of.
    [Fact]
    public async Task UploadFileToGuest_MeasuresTheLimitInBytesRatherThanCharacters()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Options.FileUploadMaxBytes = 5;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cluster.Service().UploadFileToGuest(
                info, "/tmp/f.txt", new MemoryStream(Encoding.UTF8.GetBytes("héllo"))));

        Assert.StartsWith("QGA file-write payload 6 bytes exceeds 5-byte limit.", ex.Message);
    }

    // What is reported back is the byte count while what went over the wire is the decoded text, so for
    // anything outside ASCII the number the caller is shown is larger than the string Proxmox was sent.
    // That is the honest number - it is what the limit was checked against - but it is not the length of
    // the file the guest ends up with.
    [Fact]
    public async Task UploadFileToGuest_ReportsBytesWhileTheWireCarriesTheDecodedText()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Accepts($"POST {FakeProxmoxCluster.VmPath(info, FileWrite)}");

        var reported = await cluster.Service().UploadFileToGuest(
            info, "/tmp/f.txt", new MemoryStream(Encoding.UTF8.GetBytes("héllo")));

        Assert.Equal($"wrote 6 bytes to /tmp/f.txt on vmid={Vmid}", reported);
        Assert.Equal(
            """{"content":"héllo","file":"/tmp/f.txt"}""",
            cluster.Request(HttpMethod.Post, FakeProxmoxCluster.VmPath(info, FileWrite)).Body);
    }

    [Fact]
    public async Task UploadFileToGuest_WhenProxmoxRefusesTheWrite_ThrowsNamingTheVmidAndThePath()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Rejects($"POST {FakeProxmoxCluster.VmPath(info, FileWrite)}", "Permission denied");

        var ex = await Assert.ThrowsAsync<Exception>(
            () => cluster.Service().UploadFileToGuest(
                info, "/tmp/f.txt", new MemoryStream(Encoding.UTF8.GetBytes("x"))));

        Assert.StartsWith($"QGA file-write failed for vmid={Vmid} path=/tmp/f.txt: ", ex.Message);
        Assert.Contains("Permission denied", ex.Message);
    }

    #endregion

    #region EnsureQemu

    // The guest agent is a QEMU feature - an LXC container has no QGA at all - so all four methods refuse a
    // container up front, naming themselves so the log says which operation was attempted. Refused before
    // anything is sent, and before the tokenizer runs, so a container plus a malformed argument string is
    // reported as the container.
    //
    // MountIso makes the same check with a BadRequestException instead, which is an inconsistency rather
    // than a design: the same impossibility answers the caller 400 through one route and 500 through these
    // four. MountIso's own comment argues the 400 is the right one, which would make these the ones to
    // change.
    [Theory]
    [InlineData(nameof(IProxmoxService.RunGuestProcess))]
    [InlineData(nameof(IProxmoxService.RunGuestProcessFast))]
    [InlineData(nameof(IProxmoxService.ReadGuestFile))]
    [InlineData(nameof(IProxmoxService.UploadFileToGuest))]
    public async Task EveryGuestAgentMethod_OnAnLxcContainer_IsRefusedBeforeAnythingIsSent(string operation)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.LXC);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Invoke(cluster.Service(), info, operation));

        Assert.Equal($"{operation} is only supported on QEMU VMs (vmid={Vmid} is LXC).", ex.Message);
        Assert.Empty(cluster.Http.Sent);
    }

    // The check is on the type the caller holds rather than on what the cluster reports, so it costs no
    // request either way - a QEMU machine passes it without the cluster being consulted about the type.
    [Fact]
    public async Task RunGuestProcessFast_OnAQemuVm_PassesTheTypeCheckWithoutConsultingTheCluster()
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid, ProxmoxVmType.QEMU);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");

        await cluster.Service().RunGuestProcessFast(info, Bash, null);

        Assert.DoesNotContain(FakeProxmoxCluster.ClusterResources, cluster.Http.Paths);
    }

    #endregion

    /// <summary>
    /// The tokens the exec request carried after the program in slot 0, which is the only place the private
    /// tokenizer's output can be observed. Driven through <c>RunGuestProcessFast</c> because it sends the
    /// one request and needs no exec-status arrangement.
    /// </summary>
    private static async Task<string[]> Tokens(string arguments)
    {
        var cluster = new FakeProxmoxCluster();
        var info = cluster.Has(Vmid);
        cluster.Answers($"POST {FakeProxmoxCluster.VmPath(info, Exec)}", """{"pid":4242}""");

        await cluster.Service().RunGuestProcessFast(info, Bash, arguments);

        return Command(cluster.Request(HttpMethod.Post, FakeProxmoxCluster.VmPath(info, Exec)))[1..];
    }

    /// <summary>The <c>command</c> array out of an <c>agent/exec</c> request body.</summary>
    private static string[] Command(TestHttpHandler.SentRequest request)
    {
        using var body = JsonDocument.Parse(request.Body);

        return body.RootElement.GetProperty("command")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToArray();
    }

    /// <summary>
    /// The four guest-agent methods behind their own names, so the type check they share can be stated once
    /// as a Theory. Cast to <see cref="Task"/> because the three return types have no common one.
    /// </summary>
    private static Task Invoke(IProxmoxService service, ProxmoxVmInfo info, string operation) =>
        operation switch
        {
            nameof(IProxmoxService.RunGuestProcess) =>
                (Task)service.RunGuestProcess(info, Bash, "-c hello", TimeSpan.FromSeconds(5)),
            nameof(IProxmoxService.RunGuestProcessFast) =>
                service.RunGuestProcessFast(info, Bash, "-c hello"),
            nameof(IProxmoxService.ReadGuestFile) =>
                service.ReadGuestFile(info, "/tmp/f.txt"),
            nameof(IProxmoxService.UploadFileToGuest) =>
                service.UploadFileToGuest(info, "/tmp/f.txt", new MemoryStream()),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
}
