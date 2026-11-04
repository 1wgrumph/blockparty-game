using System;
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.Persistence
{
    // Same atomic SAVE payload as rider positions and accepted queues; action IDs remap on load.
    internal static class BicycleSave
    {
        static JsonObject Pose(ApproachPose p) { var r=new JsonObject();r.Add("x",p.X);r.Add("y",p.Y);r.Add("z",p.Z);return r; }
        static ApproachPose Pose(JsonObject r) { return new ApproachPose(r.GetFloat("x","bicycles"),r.GetFloat("y","bicycles"),r.GetFloat("z","bicycles")); }
        static JsonArray Strings(IEnumerable<string> values) { var a=new JsonArray();foreach(var s in values)a.Add(new JsonString(s));return a; }
        internal static JsonObject Write(BicycleTravel b)
        {
            var r=new JsonObject();var nodes=new JsonArray();var edges=new JsonArray();var sites=new JsonArray();var racks=new JsonArray();var active=new JsonArray();var preferences=new JsonArray();var completed=new JsonArray();
            foreach(var n in b.nodes){var row=Pose(n.Value);row.Add("id",n.Key);nodes.Add(row);}
            foreach(var e in b.edges){var row=new JsonObject();row.Add("id",e.Id);row.Add("from",e.From);row.Add("to",e.To);edges.Add(row);}
            foreach(var s in b.sites){var row=new JsonObject();row.Add("id",s.Id);row.Add("rack",s.Rack);row.Add("node",s.Node);row.Add("entry",Pose(s.Entry));sites.Add(row);}
            foreach(var rack in b.parked){var row=new JsonObject();row.Add("rack",rack.Key);row.Add("bikes",Strings(rack.Value));racks.Add(row);}
            foreach(var p in b.preferences){var row=new JsonObject();row.Add("resident",(long)p.Key);row.Add("enabled",p.Value);preferences.Add(row);}
            foreach(var id in b.completed)completed.Add(new JsonNumber(((long)id).ToString()));
            foreach(var t in b.active.Values)
            {
                var row=new JsonObject();row.Add("action",(long)t.Action);row.Add("resident",(long)t.Resident);row.Add("from",t.From);row.Add("to",t.To);row.Add("use",t.Use);row.Add("selection",t.Selection);row.Add("bike",t.Bike??"");row.Add("phase",t.Phase);row.Add("next",t.Next);row.Add("returning",t.Returning);row.Add("last",t.LastTime);row.Add("acquired",t.AcquiredAt);row.Add("arrived",t.ArrivedAt);row.Add("destination",Pose(t.Destination));row.Add("route",Strings(t.Route));row.Add("edges",Strings(t.EdgeIds));active.Add(row);
            }
            r.Add("nodes",nodes);r.Add("edges",edges);r.Add("sites",sites);r.Add("racks",racks);r.Add("active",active);r.Add("preferences",preferences);r.Add("completed",completed);return r;
        }
        static void Require(bool ok) { if(!ok)throw new JsonSaveException("invalid bicycle inventory or route"); }
        static ResidentId Resident(JsonObject row,WorldState world) { var id=(ResidentId)row.GetLong("resident","bicycles");ResidentRecord actor;Require(world.TryGetResident(id,out actor));return id; }
        internal static BicycleTravel Read(JsonObject r,Dictionary<long,long> map,WorldState world,long now)
        {
            var nodes=new Dictionary<string,ApproachPose>();var edges=new List<BicycleTravel.Edge>();var sites=new List<BicycleTravel.Site>();
            var a=r.GetArray("nodes","bicycles");for(int i=0;i<a.Count;i++){var n=a.GetObject(i,"bicycles.nodes");nodes.Add(n.GetString("id","bicycles"),Pose(n));}
            a=r.GetArray("edges","bicycles");var edgeIds=new HashSet<string>();for(int i=0;i<a.Count;i++){var n=a.GetObject(i,"bicycles.edges");var e=new BicycleTravel.Edge{Id=n.GetString("id","bicycles"),From=n.GetString("from","bicycles"),To=n.GetString("to","bicycles")};Require(nodes.ContainsKey(e.From)&&nodes.ContainsKey(e.To)&&edgeIds.Add(e.Id));edges.Add(e);}
            a=r.GetArray("sites","bicycles");var siteIds=new HashSet<string>();var rackIds=new HashSet<string>();for(int i=0;i<a.Count;i++){var n=a.GetObject(i,"bicycles.sites");var s=new BicycleTravel.Site{Id=n.GetString("id","bicycles"),Rack=n.GetString("rack","bicycles"),Node=n.GetString("node","bicycles"),Entry=Pose(n.GetObject("entry","bicycles"))};Require(nodes.ContainsKey(s.Node)&&siteIds.Add(s.Id)&&rackIds.Add(s.Rack));sites.Add(s);}
            Require(sites.Count>0&&siteIds.Contains("park"));for(int i=0;i<4;i++)Require(nodes.ContainsKey("loop-"+i));
            var b=new BicycleTravel(nodes,edges,sites);foreach(var q in b.parked.Values)q.Clear();
            var bikes=new HashSet<string>();var seenRacks=new HashSet<string>();a=r.GetArray("racks","bicycles");for(int i=0;i<a.Count;i++){var n=a.GetObject(i,"bicycles.racks");string rack=n.GetString("rack","bicycles");Require(rackIds.Contains(rack)&&seenRacks.Add(rack));var list=n.GetArray("bikes","bicycles");for(int j=0;j<list.Count;j++){string bike=list.GetString(j,"bicycles");Require(siteIds.Contains(bike.StartsWith("bike-",StringComparison.Ordinal)?bike.Substring(5):"")&&bikes.Add(bike));b.parked[rack].Enqueue(bike);}}
            Require(seenRacks.SetEquals(rackIds));
            a=r.GetArray("preferences","bicycles");for(int i=0;i<a.Count;i++){var n=a.GetObject(i,"bicycles.preferences");b.PreferBicycle(Resident(n,world),n.GetBool("enabled","bicycles"));}
            a=r.GetArray("completed","bicycles");for(int i=0;i<a.Count;i++){long id=a.GetLong(i,"bicycles.completed");long fresh;if(map.TryGetValue(id,out fresh))b.completed.Add((ActionInstanceId)fresh);}
            a=r.GetArray("active","bicycles");for(int i=0;i<a.Count;i++)
            {
                var n=a.GetObject(i,"bicycles.active");long action=n.GetLong("action","bicycles"),fresh;bool mapped=map.TryGetValue(action,out fresh);
                var t=new BicycleTravel.Trip{Action=(ActionInstanceId)(mapped?fresh:action),Resident=Resident(n,world),From=n.GetString("from","bicycles"),To=n.GetString("to","bicycles"),Use=n.GetString("use","bicycles"),Selection=n.GetString("selection","bicycles"),Bike=n.GetString("bike","bicycles"),Phase=(int)n.GetLong("phase","bicycles"),Next=(int)n.GetLong("next","bicycles"),Returning=n.GetBool("returning","bicycles"),LastTime=n.GetLong("last","bicycles"),AcquiredAt=n.GetLong("acquired","bicycles"),ArrivedAt=n.GetLong("arrived","bicycles"),Destination=Pose(n.GetObject("destination","bicycles"))};
                Require(siteIds.Contains(t.From)&&siteIds.Contains(t.To)&&t.Phase>=0&&t.Phase<=2&&t.LastTime<=now&&t.AcquiredAt<=now&&t.ArrivedAt<=now&&(t.Use=="leisure"||t.Use=="transport")&&(t.Selection=="player"||t.Selection=="autonomy"));
                var route=n.GetArray("route","bicycles");for(int j=0;j<route.Count;j++){string node=route.GetString(j,"bicycles");Require(nodes.ContainsKey(node));t.Route.Add(node);}
                route=n.GetArray("edges","bicycles");for(int j=0;j<route.Count;j++){string edge=route.GetString(j,"bicycles");Require(edgeIds.Contains(edge));t.EdgeIds.Add(edge);}
                Require(t.Next>=0&&t.Next<=t.Route.Count);
                if(t.Phase==1)Require(siteIds.Contains(t.Bike.StartsWith("bike-",StringComparison.Ordinal)?t.Bike.Substring(5):"")&&bikes.Add(t.Bike));
                if(t.Bike.Length==0)t.Bike=null;
                b.active.Add(t.Resident,t);b.trips.Add(t);
            }
            Require(bikes.Count==sites.Count);return b;
        }
    }
}
