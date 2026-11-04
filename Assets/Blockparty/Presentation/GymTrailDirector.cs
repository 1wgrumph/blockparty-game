using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;
using Blockparty.Persistence;
using UnityEngine;
using UnityEngine.AI;

// Playable #251 scene. Evidence collection calls the same Choose and Step
// methods as its controls; it never sets a resident's pose during a trip.
public sealed class GymTrailDirector : MonoBehaviour, ISceneContent
{
    [Serializable] public class Point { public string id; public float[] position; }
    [Serializable] public class Link { public string id, from, to; }
    [Serializable] public class Place { public string id, rack, node; public float[] entry; }
    [Serializable] public class Data { public Point[] nodes; public Link[] edges; public Place[] sites; public float[] gym; public float[] walk_distances; }
    public bool bindToAlpha;
    public TextAsset configuration;
    public NavMeshData navigation;
    public AnimationClip[] clips;
    public Transform[] handWeights, floorWeights;
    bool useBicycle=true;
    public Transform residentRoot, bicycleRoot;
    public Data Layout { get; private set; }
    public WorldState World { get; private set; }
    public Clock Clock { get; private set; }
    public CommandArbitrator Commands { get; private set; }
    public ActivityExecutor Executor { get; private set; }
    public BicycleTravel Bikes { get; private set; }
    public ResidentId Resident { get; private set; }
    public readonly Dictionary<string,ObjectId> Destinations=new Dictionary<string,ObjectId>();
    public readonly Dictionary<string,ObjectId> Racks=new Dictionary<string,ObjectId>();
    public ObjectId Gym { get; private set; }
    public ActionInstanceId LastAction { get; private set; }
    public string Notice { get; private set; }
    Autonomy autonomy;
    Blockparty.Activities.Health health;
    public Blockparty.Activities.Health Health { get { return health; } }
    long sequence;
    Vector3 previousPosition;
    Quaternion heading=Quaternion.identity;
    bool initialized, sharedClock;
    float gaitDistance;
    readonly Dictionary<string,Transform> parkedBicycles=new Dictionary<string,Transform>();
    public static ApproachPose P(float[] p) { return new ApproachPose(p[0],p[1],p[2]); }
    public static Vector3 V(ApproachPose p) { return new Vector3(p.X,p.Y,p.Z); }
    void Start() { if(!initialized && !bindToAlpha)Initialize("apartment"); }
    public void Initialize(string start)
    {
        if(navigation!=null)NavMesh.AddNavMeshData(navigation,new Vector3(0,-.025f,0),Quaternion.identity);
        Layout=JsonUtility.FromJson<Data>(configuration.text);
        World=WorldState.CreateSeeded(251,42,0,null);
        var home=World.AllocateHome(StateWriter.SimCore,8);var house=World.AllocateHousehold(StateWriter.SimCore,"Trail residents",home.Id);
        Resident=World.AllocateResident(StateWriter.SimCore,"Rider",LifeStage.Adult,house.Id,home.Id).Id;
        Clock=new Clock(World);Commands=new CommandArbitrator(World,Clock);Executor=new ActivityExecutor(World,Commands,Clock);autonomy=new Autonomy(World,Commands);health=new Blockparty.Activities.Health(World,Commands,Executor);
        Connect(start,true);
    }
    // Select which resident the controls direct (another resident of the same world).
    public void Control(ResidentId resident) { ResidentRecord record;if(!World.TryGetResident(resident,out record))throw new ArgumentException("Unknown resident");Resident=resident;useBicycle=Bikes.PrefersBicycle(resident); }
    public void Bind(WorldState world,Clock clock,CommandArbitrator commands,ActivityExecutor executor,ResidentId resident)
    {
        World=world;Clock=clock;Commands=commands;Executor=executor;Resident=resident;sharedClock=true;
        Layout=JsonUtility.FromJson<Data>(configuration.text);autonomy=new Autonomy(World,Commands);health=new Blockparty.Activities.Health(World,Commands,Executor);
        if(navigation!=null && !initialized)NavMesh.AddNavMeshData(navigation,new Vector3(0,-.025f,0),Quaternion.identity);
        Connect(null,Destinations.Count==0);
    }
    void Connect(string start,bool register)
    {
        var nodes=new Dictionary<string,ApproachPose>();foreach(var n in Layout.nodes)nodes.Add(n.id,P(n.position));
        var edges=new List<BicycleTravel.Edge>();foreach(var e in Layout.edges)edges.Add(new BicycleTravel.Edge{Id=e.id,From=e.from,To=e.to});
        var sites=new List<BicycleTravel.Site>();foreach(var s in Layout.sites)
        {
            sites.Add(new BicycleTravel.Site{Id=s.id,Rack=s.rack,Node=s.node,Entry=P(s.entry)});
            if(register){Destinations.Add(s.id,Register(P(s.entry)));Racks.Add(s.id,Register(nodes[s.node]));}
        }
        Bikes=Executor.Bicycles??new BicycleTravel(nodes,edges,sites);Executor.Bicycles=Bikes;
        if(register)Gym=Register(P(Layout.gym));
        if(start!=null)Executor.SetResidentPosition(Resident,P(Array.Find(Layout.sites,s=>s.id==start).entry));
        Func<ApproachPose,ApproachPose,double,ApproachPose> walk=bindToAlpha?(Func<ApproachPose,ApproachPose,double,ApproachPose>)AlphaWalk:Walk;
        Executor.WalkStep=walk;Bikes.WalkStep=walk;useBicycle=Bikes.PrefersBicycle(Resident);initialized=true;Mirror();
    }
    public void RegisterObjects(ActivityExecutor executor)
    {
        foreach(var s in Layout.sites){executor.RegisterObject(Destinations[s.id],new[]{P(s.entry)});executor.RegisterObject(Racks[s.id],new[]{P(Array.Find(Layout.nodes,n=>n.id==s.node).position)});}
        executor.RegisterObject(Gym,new[]{P(Layout.gym)});
    }
    ObjectId Register(ApproachPose pose) { var id=World.AllocateObject(StateWriter.SimCore);Executor.RegisterObject(id,new[]{pose});return id; }
    public static ApproachPose Walk(ApproachPose a,ApproachPose b,double step)
    {
        if(Vector3.Distance(V(a),V(b))<.001f)return b;
        var path=new NavMeshPath();if(!NavMesh.CalculatePath(V(a),V(b),1,path) || path.status!=NavMeshPathStatus.PathComplete)throw new InvalidOperationException("No complete walking path "+V(a)+" -> "+V(b));
        var current=V(a);
        foreach(var corner in path.corners)
        {
            // Navmesh voxel lift is within NAV's arrival tolerance; preserve
            // the authored floor height while following the observed XZ turns.
            var target=new Vector3(corner.x,current.y,corner.z);float length=Vector3.Distance(current,target);
            if(length<.005f)continue;
            current=Vector3.MoveTowards(current,target,(float)step);step-=length;if(step<=0)break;
        }
        if(Vector3.Distance(new Vector3(current.x,b.Y,current.z),V(b))<.05f)return b;
        return new ApproachPose(current.x,current.y,current.z);
    }
    // The GYM-TRAIL NavMesh covers only its own paving (area mask 1), not the
    // Alpha's ground, homes or stages. A walk to a GYM-TRAIL point from a pose
    // on that paving, including every intermediate step, keeps the
    // complete-path check; a walk from an Alpha pose off the paving keeps the
    // executor's straight walk (ActivityExecutor.AdvanceTravel arithmetic).
    ApproachPose AlphaWalk(ApproachPose a,ApproachPose b,double step)
    {
        if(OwnsPoint(b) && OnPaving(a))return Walk(a,b,step);
        double dx=(double)b.X-(double)a.X,dy=(double)b.Y-(double)a.Y,dz=(double)b.Z-(double)a.Z,remaining=Math.Sqrt(dx*dx+dy*dy+dz*dz);
        if(remaining<=step)return b;
        double ratio=step/remaining;
        return new ApproachPose((float)((double)a.X+dx*ratio),(float)((double)a.Y+dy*ratio),(float)((double)a.Z+dz*ratio));
    }
    bool OwnsPoint(ApproachPose p)
    {
        var v=V(p);
        if(Vector3.Distance(v,V(P(Layout.gym)))<.001f)return true;
        foreach(var n in Layout.nodes)if(Vector3.Distance(v,V(P(n.position)))<.001f)return true;
        foreach(var s in Layout.sites)if(Vector3.Distance(v,V(P(s.entry)))<.001f)return true;
        return false;
    }
    // On the walking area (mask 1) directly below the pose, not merely near it.
    static bool OnPaving(ApproachPose p)
    {
        var v=V(p);NavMeshHit hit;
        return NavMesh.SamplePosition(v,out hit,.5f,1) && new Vector2(hit.position.x-v.x,hit.position.z-v.z).magnitude<.01f;
    }
    public bool Choose(string use,string site,bool autonomous)
    {
        if(Commands.GetCurrent(Resident)!=null || Commands.GetPending(Resident).Length>0)return false;
        string activity=use=="gym"?"ACT-SPORT-01":use=="leisure"?"ACT-SPORT-03":null;
        ObjectId target=use=="gym"?Gym:use=="leisure"?Racks[site]:Destinations[site];
        if(autonomous)
        {
            if(use=="transport") { if(!Commands.EnqueueAutonomousTravel(Resident,target))return false; }
            else { var d=autonomy.Decide(Resident,new[]{new AvailableAction(new ActivityId(activity),(long)target)},null);if(!d.CommittedEffect.StartsWith("Enqueued:",StringComparison.Ordinal))return false; }
            Commands.PromoteNext(Resident);
        }
        else
        {
            ResidentRecord actor;World.TryGetResident(Resident,out actor);
            var result=health.Submit(new CommandEnvelope((CommandId)(++sequence),World.Epoch,World.GameSeconds,CommandSource.PlayerHuman,actor.ResidentSequence+1u, use=="transport"?CommandKind.TravelTo:CommandKind.DirectActivity,Resident,activity==null?(ActivityId?)null:new ActivityId(activity),(long)target,
                use=="transport"?(CommandParameters)new CommandParameters.Travel(Blockparty.Contracts.QueueMode.Immediate,TravelTargetKind.Object):new CommandParameters.Direct(Blockparty.Contracts.QueueMode.Immediate)));
            if(!result.Accepted){Notice=result.Reason.ToString();return false;}
        }
        LastAction=Commands.GetCurrent(Resident).Action;health.Tick(StateWriter.ActivityExecutor);Notice=use+" → "+site;return true;
    }
    public void Step(double realSeconds)
    {
        Clock.AdvanceRealSeconds(StateWriter.SimCore,realSeconds,delegate { health.Tick(StateWriter.ActivityExecutor); });Mirror();
    }
    void Update() { if(initialized && !sharedClock)Step(Mathf.Min(Time.deltaTime,.25f)); }
    void LateUpdate() { if(initialized && sharedClock)Mirror(); }
    void Mirror()
    {
        var position=V(Executor.GetResidentPosition(Resident));var direction=position-previousPosition;
        if(direction.sqrMagnitude>.000001f)heading=Quaternion.LookRotation(-direction.normalized,Vector3.up);
        previousPosition=position;
        var latest=Bikes.ActiveTrip(Resident);
        var command=Commands.GetCurrent(Resident);
        string clipName="cycling-approach";
        if(direction.magnitude<1f)gaitDistance+=direction.magnitude;
        float time=0;
        if(Layout.walk_distances!=null && Layout.walk_distances.Length==17)
        {
            var distances=Layout.walk_distances;float phase=gaitDistance%Mathf.Max(.01f,distances[16]);
            for(int k=1;k<17;k++)if(phase<=distances[k]){time=((k-1)+Mathf.InverseLerp(distances[k-1],distances[k],phase))/8f;break;}
        }
        if(direction.sqrMagnitude>.000001f)heading=Quaternion.LookRotation(direction.normalized,Vector3.up);
        if(command!=null && command.Activity.Code=="ACT-SPORT-01" && Executor.GetExecution(command.Action)!=null && Executor.GetExecution(command.Action).Stage==ExecutionStage.Perform){clipName="gym-loop";heading=Quaternion.identity;}
        else if(latest!=null && latest.Riding)
        {
            heading=Quaternion.LookRotation(V(Bikes.RideDirection(Resident)),Vector3.up);
            time=(World.GameSeconds/60f)%2;
            if(World.GameSeconds<latest.AcquiredAt+120){clipName="cycling-start";time=(World.GameSeconds-latest.AcquiredAt)/60f;}
            else if(latest.ArrivedAt>0){clipName="cycling-finish";time=(World.GameSeconds-latest.ArrivedAt)/60f;}
            else clipName="cycling-loop";
        }
        if(residentRoot!=null)
        {
            if(clips!=null)foreach(var clip in clips)if(clip!=null && clip.name==clipName){clip.SampleAnimation(residentRoot.gameObject,time);break;}
            residentRoot.position=position;residentRoot.rotation=heading;
        }
        if(handWeights!=null)foreach(var weight in handWeights)weight.gameObject.SetActive(clipName=="gym-loop");
        if(floorWeights!=null)foreach(var weight in floorWeights)weight.gameObject.SetActive(clipName!="gym-loop");
        if(bicycleRoot!=null)
        {
            var parked=Bikes.ParkedPositions();
            foreach(var pair in parked)
            {
                Transform visual;
                if(!parkedBicycles.TryGetValue(pair.Key,out visual))
                {
                    visual=Instantiate(bicycleRoot.gameObject,bicycleRoot.parent).transform;
                    visual.name=pair.Key;parkedBicycles.Add(pair.Key,visual);
                }
                visual.position=V(pair.Value);visual.rotation=Quaternion.identity;visual.gameObject.SetActive(true);
            }
            foreach(var pair in parkedBicycles)if(!parked.ContainsKey(pair.Key))pair.Value.gameObject.SetActive(false);
            bicycleRoot.gameObject.SetActive(latest!=null && latest.Riding);
            if(latest!=null && latest.Riding)
            {
                bicycleRoot.position=position;bicycleRoot.rotation=heading;
                foreach(var part in bicycleRoot.GetComponentsInChildren<Transform>())if(part.name=="bike_drive")part.localRotation=Quaternion.AngleAxis(time*180+(clipName=="cycling-start"?60:clipName=="cycling-loop"?0:120),Vector3.right);
            }
        }
    }
    // Set by AlphaDirector when its HUD draws this panel's controls (POLISH-HUD #216).
    public bool hudOwnsPanel;
    void OnGUI()
    {
        if(!initialized || hudOwnsPanel)return;
        GUILayout.BeginArea(new Rect(sharedClock?Screen.width-292:12,12,280,650),GUI.skin.box);
        GUILayout.Label("Neighborhood gym and bike trail");GUILayout.Label(Notice??"Choose a destination");
        useBicycle=GUILayout.Toggle(useBicycle,"Use a bicycle for travel");Bikes.PreferBicycle(Resident,useBicycle);
        foreach(var s in Layout.sites)if(GUILayout.Button("Travel to "+s.id))Choose("transport",s.id,false);
        if(GUILayout.Button("Ride bike for leisure"))Choose("leisure",Nearest(),false);
        if(GUILayout.Button("Work out at apartment gym"))Choose("gym","apartment",false);
        if(GUILayout.Button("Autonomy: choose leisure or gym"))Choose(World.GameSeconds%2==0?"leisure":"gym",Nearest(),true);
        if(!sharedClock && GUILayout.Button("Save rider and bikes")){var r=SaveV1.SaveGame(new GameStack(World,Clock,Commands,Executor,new AppearanceStore()),System.IO.Path.Combine(Application.persistentDataPath,"gym-trail-save"));Notice=r.Ok?"Saved":r.Detail;}
        if(!sharedClock && GUILayout.Button("Load rider and bikes")){var r=SaveV1.LoadGame(System.IO.Path.Combine(Application.persistentDataPath,"gym-trail-save"),this);if(r.Ok){var g=r.Game;Bind(g.World,g.Clock,g.Arbitrator,g.Executor,Resident);sharedClock=false;}Notice=r.Ok?"Loaded":r.Notice;}
        if(GUILayout.Button("Cancel current action"))
        {
            ResidentRecord actor;World.TryGetResident(Resident,out actor);
            Commands.Submit(new CommandEnvelope((CommandId)(++sequence),World.Epoch,World.GameSeconds,CommandSource.PlayerHuman,actor.ResidentSequence+1u,CommandKind.CancelCurrent,Resident,null,null,CommandParameters.None.Instance));
        }
        if(GUILayout.Button(Clock.Paused?"Resume":"Pause")){if(Clock.Paused)Clock.Resume();else Clock.Pause();}
        GUILayout.EndArea();
    }
    string Nearest() { var p=V(Executor.GetResidentPosition(Resident));Place best=Layout.sites[0];foreach(var s in Layout.sites)if(Vector3.Distance(p,V(P(s.entry)))<Vector3.Distance(p,V(P(best.entry))))best=s;return best.id; }
}
