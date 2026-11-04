#!/usr/bin/env python3
"""Run the plain C# package suites with the pinned Unity Roslyn/runtime."""
import argparse,pathlib,subprocess,os,sys,tempfile
ap=argparse.ArgumentParser()
ap.add_argument('--unity',type=pathlib.Path,required=True,help='Unity installation root')
ap.add_argument('--output',type=pathlib.Path,default=pathlib.Path(tempfile.gettempdir())/'blockparty-m0-suites')
ap.add_argument('--only',default=None,help='compile and run a single suite stem only')
ap.add_argument('--skip',action='append',default=[],help='leave out this suite stem (repeatable)')
args=ap.parse_args()
root=pathlib.Path(__file__).resolve().parents[2]; out=args.output; out.mkdir(parents=True,exist_ok=True)
u=args.unity/'Editor/Data'; runner=str(u/'netcorerun/netcorerun')
sources=[str(p) for p in (root/'Assets/Blockparty').rglob('*.cs') if not any(x in p.parts for x in ['Editor','Presentation'])]
refs=['System.Private.CoreLib','System.Runtime','System.Console','System.Reflection.Primitives','System.IO','System.IO.FileSystem','System.IO.FileSystem.Primitives','System.Diagnostics.Process','System.ComponentModel.Primitives','System.Text.Json','System.Memory','System.Collections','System.Security.Cryptography.Algorithms','System.Security.Cryptography.Primitives']
env=os.environ.copy(); env.pop('MODEL_API_KEY',None)
tests=[t for t in sorted((root/'tests/production').glob('*.cs')) if t.stem not in args.skip]
selectors=('LOOK-FINAL-APPROVE',)
if args.only:
 tests=[t for t in tests if t.stem==args.only]
 if not tests and args.only not in selectors: print(args.only,'missing'); sys.exit(1)
 if args.only=='TOOLCHAIN': print('TOOLCHAIN is Unity-editor-only; refusing standalone selector without running'); sys.exit(2)
failed=[]
for test in tests:
 if test.stem=='TOOLCHAIN': continue
 exe=out/(test.stem+'.exe')
 if test.stem in ['CLOCK','STATE','RESIDENT-CREATE']:
  names={'CLOCK':['BoundaryTypes.cs','WorldState.cs','Clock.cs'],'STATE':['BoundaryTypes.cs','WorldState.cs'],'RESIDENT-CREATE':['BoundaryTypes.cs','WorldState.cs','ResidentFactory.cs']}[test.stem]
  selected=[s for s in sources if pathlib.Path(s).name in names]
 else: selected=sources
 cmd=[runner,str(u/'DotNetSdkRoslyn/csc.dll'),'-nologo','-t:exe','-langversion:9','-out:'+str(exe),'-lib:'+str(u/'netcorerun')]+['-r:'+r+'.dll' for r in refs]+selected+[str(test)]+([str(root/"tests/production/repair/M1Regressions.cs")] if test.stem in ["ECONOMY","BOT","RELATION","TIERS","FOOD","UI-TITLE","UI-NAV","SAVE"] else [])
 c=subprocess.run(cmd,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,env=env); (out/(test.stem+'-compile.log')).write_text(c.stdout)
 print(test.stem,'compile',c.returncode,flush=True)
 if c.returncode: print(c.stdout); failed.append(test.stem); continue
 c=subprocess.run([runner,str(exe)],stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,env=env,timeout=180); (out/(test.stem+'.log')).write_text(c.stdout)
 print(test.stem,'run',c.returncode,'; '.join(c.stdout.splitlines()[-3:]),flush=True)
 if c.returncode: failed.append(test.stem)
for package in selectors:
 if (args.only and args.only!=package) or package in args.skip: continue
 c=subprocess.run([sys.executable,str(root/'tools/verify.py'),'--package',package,'--seit',str(root/'docs/plans/issue-3-specification/seit.json')],cwd=root,stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,env=env,timeout=180)
 (out/(package+'.log')).write_text(c.stdout)
 print(package,'run',c.returncode,'; '.join(c.stdout.splitlines()[-3:]),flush=True)
 if c.returncode: failed.append(package)
print('FAILED',failed); sys.exit(bool(failed))
