using System;
using System.Collections.Generic;
using Blockparty.Contracts;

namespace Blockparty.Simulation
{
    // Instance-owned vehicles and routes; all motion uses the shared simulation clock.
    public sealed class BicycleTravel
    {
        public sealed class Site
        {
            public string Id, Rack, Node;
            public ApproachPose Entry;
        }
        public sealed class Edge
        {
            public string Id, From, To;
        }
        public sealed class Event
        {
            public string Kind, Bike, Rack, Owner, ResidentBike;
            public ApproachPose Position;
        }
        public sealed class Trip
        {
            public ActionInstanceId Action;
            public ResidentId Resident;
            public string From, To, Selection, Use, Bike;
            public readonly List<Event> Events = new List<Event>();
            public readonly List<string> EdgeIds = new List<string>();
            public readonly List<ApproachPose> RidePositions = new List<ApproachPose>();
            internal readonly List<string> Route = new List<string>();
            internal int Next;
            internal int Phase;
            internal bool Returning;
            internal long LastTime;
            public long AcquiredAt, ArrivedAt;
            public bool Riding { get { return Phase == 1; } }
            internal ApproachPose Destination;
            public bool Complete { get { return Phase == 3; } }
        }
        internal readonly Dictionary<string,ApproachPose> nodes;
        internal readonly List<Edge> edges;
        internal readonly List<Site> sites;
        internal readonly Dictionary<string,Queue<string>> parked = new Dictionary<string,Queue<string>>();
        internal readonly HashSet<ActionInstanceId> completed = new HashSet<ActionInstanceId>();
        internal readonly Dictionary<ResidentId,Trip> active = new Dictionary<ResidentId,Trip>();
        internal readonly List<Trip> trips = new List<Trip>();
        internal readonly Dictionary<ResidentId,bool> preferences = new Dictionary<ResidentId,bool>();
        public Func<ApproachPose, ApproachPose, double, ApproachPose> WalkStep { get; set; }
        public IReadOnlyList<Trip> Trips { get { return trips.AsReadOnly(); } }

        public BicycleTravel(Dictionary<string,ApproachPose> nodes, List<Edge> edges, List<Site> sites)
        {
            this.nodes=nodes; this.edges=edges; this.sites=sites;
            foreach(var site in sites){var q=new Queue<string>();q.Enqueue("bike-"+site.Id);parked.Add(site.Rack,q);}
        }
        public void PreferBicycle(ResidentId resident,bool enabled) { preferences[resident]=enabled; }
        public bool PrefersBicycle(ResidentId resident) { bool enabled;return !preferences.TryGetValue(resident,out enabled) || enabled; }
        public Trip ActiveTrip(ResidentId resident) { Trip trip;return active.TryGetValue(resident,out trip)?trip:null; }
        public ApproachPose RideDirection(ResidentId resident)
        {
            var t=ActiveTrip(resident);if(t==null || t.Route.Count==0)return new ApproachPose(0,0,-1);
            int i=Math.Min(t.Next,t.Route.Count-1);
            var a=i==0?nodes[Named(t.From).Node]:nodes[t.Route[i-1]];var b=nodes[t.Route[i]];
            return new ApproachPose(a.X-b.X,0,a.Z-b.Z);
        }
        public Dictionary<string,ApproachPose> ParkedPositions()
        {
            var positions=new Dictionary<string,ApproachPose>();
            foreach(var site in sites)
            {
                int slot=0;var p=nodes[site.Node];
                foreach(var bike in parked[site.Rack])positions.Add(bike,new ApproachPose(p.X+1.4f,p.Y,p.Z+(slot++-.5f)*.45f));
            }
            return positions;
        }
        static double Distance(ApproachPose a,ApproachPose b) { double x=a.X-b.X,y=a.Y-b.Y,z=a.Z-b.Z;return Math.Sqrt(x*x+y*y+z*z); }
        Site Closest(ApproachPose p) { Site best=sites[0];foreach(var s in sites)if(Distance(p,s.Entry)<Distance(p,best.Entry))best=s;return best; }
        Site Named(string id) { return sites.Find(s=>s.Id==id); }
        List<Edge> Path(string from,string to)
        {
            var previous=new Dictionary<string,Edge>(); var queue=new Queue<string>();queue.Enqueue(from);previous.Add(from,null);
            while(queue.Count>0 && !previous.ContainsKey(to))
            {
                string n=queue.Dequeue();foreach(var e in edges)
                {
                    string next=e.From==n?e.To:e.To==n?e.From:null;
                    if(next!=null && !previous.ContainsKey(next)){previous.Add(next,e);queue.Enqueue(next);}
                }
            }
            if(!previous.ContainsKey(to))throw new InvalidOperationException("No bicycle route "+from+" -> "+to);
            var path=new List<Edge>();while(to!=from){var e=previous[to];path.Add(e);to=e.From==to?e.To:e.From;}path.Reverse();return path;
        }
        void Append(Trip trip,IEnumerable<Edge> path,ref string current)
        {
            foreach(var e in path){trip.EdgeIds.Add(e.Id);current=e.From==current?e.To:e.From;trip.Route.Add(current);}
        }
        Trip Begin(ExecutionRecord record,ActionOwner owner,ApproachPose current,ApproachPose destination,bool leisure,long now)
        {
            bool enabled; if(!leisure && preferences.TryGetValue(record.Resident,out enabled) && !enabled)return null;
            Site from=Closest(current), to=leisure?from:Closest(destination);
            if(parked[from.Rack].Count==0 || (!leisure && from==to))return null;
            var trip=new Trip{Action=record.Action,Resident=record.Resident,From=from.Id,To=to.Id,Use=leisure?"leisure":"transport",Selection=owner==ActionOwner.Player?"player":"autonomy",Destination=destination,LastTime=now};
            string node=from.Node;
            if(leisure)
            {
                // The leisure circuit rides into the park through its entrance spur.
                Append(trip,Path(node,nodes.ContainsKey("entry-park")?"entry-park":Named("park").Node),ref node);
                Append(trip,Path(node,"loop-0"),ref node);
                for(int i=1;i<=4;i++)Append(trip,Path(node,"loop-"+(i%4)),ref node);
                Append(trip,Path(node,from.Node),ref node);
            }
            else Append(trip,Path(node,to.Node),ref node);
            trip.Events.Add(new Event{Kind="selected",Position=current});
            active.Add(record.Resident,trip);trips.Add(trip);return trip;
        }
        public string OwnerOf(string bike)
        {
            if(bike==null)return null;
            foreach(var rack in parked)if(rack.Value.Contains(bike))return rack.Key;
            foreach(var trip in active.Values)if(trip.Phase==1 && trip.Bike==bike)return ((long)trip.Resident).ToString();
            return null;
        }
        public string ResidentBicycle(ResidentId resident) { Trip trip;return active.TryGetValue(resident,out trip) && trip.Phase==1?trip.Bike:null; }
        void Record(Trip t,string kind,ApproachPose p,string rack=null)
        {
            t.Events.Add(new Event{Kind=kind,Position=p,Rack=rack,Bike=t.Bike,Owner=OwnerOf(t.Bike),ResidentBike=ResidentBicycle(t.Resident)});
        }
        static ApproachPose Move(ApproachPose a,ApproachPose b,ref double distance)
        {
            double left=Distance(a,b);if(left<=distance){distance-=left;return b;}
            double r=distance/left;distance=0;return new ApproachPose((float)(a.X+(b.X-a.X)*r),(float)(a.Y+(b.Y-a.Y)*r),(float)(a.Z+(b.Z-a.Z)*r));
        }
        ApproachPose Advance(Trip t,ApproachPose p,long now)
        {
            double minutes=Math.Max(0,now-t.LastTime)/60.0;t.LastTime=now;
            if(t.Phase==0)
            {
                double step=minutes;var site=Named(t.From);p=WalkStep==null?Move(p,nodes[site.Node],ref step):WalkStep(p,nodes[site.Node],step);
                if(Distance(p,nodes[site.Node])>.00001)return p;
                Record(t,"walk-to-rack",p,site.Rack);
                if(parked[site.Rack].Count==0){t.Phase=3;return p;}
                t.Bike=parked[site.Rack].Dequeue();t.AcquiredAt=now;t.Phase=1;Record(t,"take-bike",p,site.Rack);t.RidePositions.Add(p);
                return p;
            }
            if(t.Phase==1)
            {
                if(now<t.AcquiredAt+120)return p;
                double step=minutes*12;
                while(t.Next<t.Route.Count)
                {
                    var target=nodes[t.Route[t.Next]];p=Move(p,target,ref step);t.RidePositions.Add(p);
                    if(Distance(p,target)>.00001)return p;
                    t.Next++;if(step<=0)return p;
                }
                if(t.ArrivedAt==0)t.ArrivedAt=now;
                if(now<t.ArrivedAt+120)return p;
                Record(t,"ride",p);t.Phase=2;
                var site=Named(t.To);
                parked[site.Rack].Enqueue(t.Bike);Record(t,t.Use=="leisure"?"return-bike":"park-bike",p,site.Rack);
                return p;
            }
            if(t.Phase==2)
            {
                double step=minutes;p=WalkStep==null?Move(p,t.Destination,ref step):WalkStep(p,t.Destination,step);
                if(Distance(p,t.Destination)<.00001){t.Phase=3;Record(t,t.Returning?"interrupted":t.Use=="leisure"?"finished":"walk-in",p);}
            }
            return p;
        }
        public void ContinueReturns(CommandArbitrator commands,ActivityExecutor executor,long now)
        {
            var remove=new List<ResidentId>();
            foreach(var pair in active)
            {
                var t=pair.Value;var current=commands.GetCurrent(t.Resident);
                bool queued=false;foreach(var pending in commands.GetPending(t.Resident))if(pending.Action==t.Action)queued=true;
                if(!t.Returning && (queued || (current!=null && current.Action==t.Action)))continue;
                var currentPosition=executor.GetResidentPosition(t.Resident);
                if(t.Phase==0){t.Phase=3;Record(t,"interrupted",currentPosition);remove.Add(t.Resident);completed.Add(t.Action);continue;}
                if(!t.Returning && t.Phase==1 && t.Next<t.Route.Count)
                {
                    Site nearest=sites[0];foreach(var site in sites)if(Distance(currentPosition,nodes[site.Node])<Distance(currentPosition,nodes[nearest.Node]))nearest=site;
                    t.EdgeIds.RemoveRange(t.Next+1,t.EdgeIds.Count-t.Next-1);
                    string next=t.Route[t.Next];t.Route.Clear();t.Route.Add(next);t.Next=0;t.To=nearest.Id;
                    Append(t,Path(next,nearest.Node),ref next);
                }
                t.Returning=true;t.Destination=nodes[Named(t.To).Node];
                var p=Advance(t,executor.GetResidentPosition(t.Resident),now);
                executor.SetResidentPosition(t.Resident,p);
                if(t.Complete){remove.Add(t.Resident);completed.Add(t.Action);}
            }
            foreach(var resident in remove)active.Remove(resident);
        }
        public bool CanContinueLeisure(ExecutionRecord record,ApproachPose position)
        {
            return completed.Contains(record.Action) || active.ContainsKey(record.Resident) || parked[Closest(position).Rack].Count>0;
        }
        public bool IsReturning(ResidentId resident) { Trip t;return active.TryGetValue(resident,out t) && t.Returning; }
        public bool Step(ExecutionRecord record,ActionOwner owner,ApproachPose current,ApproachPose destination,bool leisure,long now,out ApproachPose next)
        {
            if(completed.Contains(record.Action)){next=current;return false;}
            Trip t;if(!active.TryGetValue(record.Resident,out t))t=Begin(record,owner,current,destination,leisure,now);
            if(t==null){next=current;return false;}
            next=Advance(t,current,now);
            if(t.Complete){active.Remove(record.Resident);completed.Add(record.Action);return false;}
            return true;
        }
    }
}
