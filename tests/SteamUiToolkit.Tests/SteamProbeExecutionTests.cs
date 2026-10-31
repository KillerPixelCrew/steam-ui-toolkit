using System.Text.Json;

namespace SteamUiToolkit.Tests;

/// <summary>Executes the actual injected expressions, including their embedded resolver and lifecycle wrappers.</summary>
/// <remarks>
///     A gate's probe is the gate's own <c>probe()</c>, so what it finds is exercised against the emitted
///     asset by the <c>eng/check-*.mjs</c> checks. What stays here is what C# builds: the gate
///     expressions that bind to the bridge and read its standard answers.
/// </remarks>
public sealed class SteamProbeExecutionTests
{
    [Fact]
    public async Task EveryBuiltInGateBindsItsProbeVerifyAndRemovalToTheGateAndItsStandardStatus()
    {
        var gates = SteamSurfaceModuleTests.BuildSurfaces(new RecordingBackend())
            .SelectMany(surface => surface.Module.Patches).OfType<SteamGatePatch>()
            .DistinctBy(gate => gate.Id).ToList();
        gates.Add((SteamGatePatch)SteamPagePatch.Create("example.page", "examplePage", "v1", "Example"));
        var expressions = new List<object>();
        foreach (var gate in gates)
        {
            await using var transport = new FakeSteamUiTransport();
            var context = new SteamUiPatchContext(transport, TimeSpan.FromSeconds(1));
            await gate.VerifyAsync(context, CancellationToken.None);
            var verify = Assert.Single(transport.Expressions);
            await gate.RemoveAsync(context, CancellationToken.None);
            expressions.Add(new
            {
                gate.Id,
                gate.GateName,
                bridgeNamespace = SteamUiBridgeIdentity.Namespace,
                probe = gate.ProbeExpression,
                verify,
                remove = transport.Expressions[1]
            });
        }

        // A stand-in gate with the standard status, registered under the patch's own gate name and
        // nothing else, so an expression bound to another name finds no gate.
        const string script = """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const results=[];
              for(const test of JSON.parse(input)){
                const window={};
                const run=expression=>JSON.parse(vm.runInNewContext(expression,{window},{timeout:1000}));
                const answers=[run(test.probe)];
                const status={ok:true,installed:true,resolved:true,held:true,claims:2,lastError:''};
                let removed=0,removeOk=true,answer={ok:true,outcome:'ready',facts:{}};
                const gate={probe:()=>answer,status:()=>status,
                  remove:()=>{removed++;return removeOk?{ok:true}:{ok:false,error:'release refused'}}};
                window[test.bridgeNamespace]={gate:name=>name===test.GateName?gate:null};
                answers.push(run(test.probe));
                answer={ok:false,outcome:'notReady',reason:'not mounted'};
                answers.push(run(test.probe));
                delete gate.probe;
                answers.push(run(test.probe));
                assert.equal(run(test.verify).ok,true,test.Id);
                status.held=false;
                assert.equal(run(test.verify).ok,false,test.Id);
                status.held=true;status.installed=false;
                assert.equal(run(test.verify).ok,false,test.Id);
                // A removal that still holds claims is not a removal, whatever it answered.
                status.claims=1;
                assert.equal(run(test.remove).ok,false,test.Id);
                status.claims=0;removeOk=false;
                const refused=run(test.remove);
                assert.equal(refused.ok,false,test.Id);
                assert.equal(refused.error,'release refused',test.Id);
                removeOk=true;
                assert.equal(run(test.remove).ok,true,test.Id);
                assert.equal(removed,3,test.Id);
                delete window[test.bridgeNamespace];
                assert.equal(run(test.verify).ok,false,test.Id);
                results.push(answers);
              }
              process.stdout.write(JSON.stringify(results));
            });
            """;
        var output = await NodeScript.RunAsync(script, expressions);
        using var results = JsonDocument.Parse(output);
        Assert.Equal(gates.Count, results.RootElement.GetArrayLength());
        for (var index = 0; index < gates.Count; index++)
        {
            var answers = results.RootElement[index];
            var gate = gates[index];
            var withoutBridge = Parse(answers[0]);
            Assert.False(withoutBridge.TargetPresent, gate.Id);
            Assert.Contains("bridge unavailable", withoutBridge.Diagnostic, StringComparison.Ordinal);
            Assert.True(Parse(answers[1]).Compatible, gate.Id);
            Assert.False(Parse(answers[2]).TargetPresent, gate.Id);
            var withoutProbe = Parse(answers[3]);
            Assert.True(withoutProbe.TargetPresent, gate.Id);
            Assert.False(withoutProbe.Compatible, gate.Id);
        }
    }

    [Fact]
    public async Task OneEvaluationAnswersEveryAbsentGateInOrder()
    {
        var expression = SteamGatePatch.ProbeAllExpression(["ready", "waiting", "throws", "absent"]);
        const string script = """
            const vm=require('node:vm');
            let input='';process.stdin.on('data',s=>input+=s);process.stdin.on('end',()=>{
              const test=JSON.parse(input),window={};
              const gates={ready:{probe:()=>({ok:true,outcome:'ready'})},
                waiting:{probe:()=>({ok:false,outcome:'notReady',reason:'not mounted'})},
                throws:{probe:()=>{throw Error('broken')}}};
              window[test.bridgeNamespace]={gate:name=>gates[name]??null};
              const answers=JSON.parse(vm.runInNewContext(test.expression,{window},{timeout:1000}));
              delete window[test.bridgeNamespace];
              const unbridged=JSON.parse(vm.runInNewContext(test.expression,{window},{timeout:1000}));
              process.stdout.write(JSON.stringify({answers,unbridged}));
            });
            """;
        var output = await NodeScript.RunAsync(script,
            new { expression, bridgeNamespace = SteamUiBridgeIdentity.Namespace });
        using var results = JsonDocument.Parse(output);
        var outcomes = results.RootElement.GetProperty("answers").EnumerateArray()
            .Select(answer => answer.GetProperty("outcome").GetString() ?? "").ToArray();
        Assert.Equal(["ready", "notReady", "incompatible", "notReady"], outcomes);
        Assert.All(results.RootElement.GetProperty("unbridged").EnumerateArray(),
            answer => Assert.Equal("notReady", answer.GetProperty("outcome").GetString()));
    }

    private static SteamUiPatchProbeResult Parse(JsonElement answer)
    {
        return SteamUiPatchEvaluation.ParseGateProbe(answer.GetRawText(), "fingerprint-v1");
    }
}
