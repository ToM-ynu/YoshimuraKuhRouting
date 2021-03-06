using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CsvHelper;
using QuickGraph;
using QuickGraph.Algorithms;
using QuickGraph.Algorithms.Observers;
using QuickGraph.Algorithms.ShortestPath;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Glitter
{

    internal class WeightedGraphSelection
    {
        internal Graph WeightedDirectedGraph { get => weightedDirectedGraph; private set => weightedDirectedGraph = value; }
        internal Graph WeightedUndirectedGraph { get => weightedUndirectedGraph; private set => weightedUndirectedGraph = value; }
        Dictionary<string, (int upper, int horizontal, int lower)> wires;
        private Graph weightedDirectedGraph;
        private Graph weightedUndirectedGraph;
        private double maxDensity;

        private Graph HorizontalConstrainGraph;

        private Dictionary<string, double> LocalMaximumDensity;

        private double MaxDensity { get => maxDensity; set => maxDensity = value; }
        internal WeightedGraphSelection(Graph weightedDirectedGraph, Graph weightedUndirectedGraph, IReadOnlyDictionary<string, double> localMaximumDensity, Dictionary<string, (int upper, int horizontal, int lower)> wires,
        Graph horizontalConstrainGraph)
        {
            WeightedDirectedGraph = new Graph(weightedDirectedGraph);
            WeightedUndirectedGraph = new Graph(weightedUndirectedGraph);
            MaxDensity = localMaximumDensity.Max(a => a.Value);
            this.wires = new Dictionary<string, (int upper, int horizontal, int lower)>(wires);
            HorizontalConstrainGraph = new Graph(horizontalConstrainGraph);
            //書き換えられることはないと思うけど、DeepCopyで
            LocalMaximumDensity = new Dictionary<string, double>(localMaximumDensity);
        }

        internal List<(string, string, double)> Selection()
        {
            var unprocessedSet = new HashSet<string>(WeightedDirectedGraph.Vertices);
            var upper = new Queue<(string, string, double)>();
            var lower = new Stack<(string, string, double)>();

            while (unprocessedSet.Count() != 2)
            {
                var flag = NodeSelection(unprocessedSet);
                var order = EdgeSelection(unprocessedSet);
                foreach (var (net, bound, height) in order)
                {
                    if (bound == "CT") upper.Enqueue((net, bound, height));
                    else if (bound == "CB") lower.Push((net, bound, height));
                    else throw new InvalidDataException();
                    unprocessedSet.Remove(net);
                }
            }
            var result = new List<(string, string, double)>();
            while (upper.Count() != 0)
            {
                result.Add(upper.Dequeue());
            }
            while (lower.Count() != 0)
            {
                result.Add(lower.Pop());
            }
            return result;
        }


        //false で帰ってきたらNodeSelectionがいる。
        //trueで帰ってきたら、Unprocessed nodeがないので終了。
        private bool NodeSelection(IReadOnlyCollection<string> unprocessedSet, bool ConsoleOut = false)
        {
            int count = 0;
            while (count++ < 1000)
            {
                var ancw = CreateChainWeight.Ancestor(WeightedDirectedGraph);
                var desw = CreateChainWeight.Deanestor(WeightedDirectedGraph);
                var LabelList = WeightedUndirectedGraph.Edges.
                Select(a => (a, CalcLabel(a.Source, a.Target, ancw, desw))).
                OrderByDescending(a => a.Item2.Weight).ToList();
                //wire の割当が終了
                if (LabelList.Count == 0)
                    return true;

                //Goto NodeSelection
                if (LabelList.First().Item2.Weight <= MaxDensity)
                    return false;
                if (LabelList.Select(a => a.Item2.Source).Contains("-1"))
                    return false;
                var hoge = LabelList.First();
                AddEdgeWeightedDirectedGraph(hoge.a, hoge.Item2);
#if DEBUG
                if (ConsoleOut == true)
                {
                    Console.Write("Labels\t");
                    LabelList.Select(a => a.Item2).ToString<(double, string, string)>().Write();
                    Console.Write("ANCW\t");
                    ancw.ToString<string, double>().Write();
                    Console.Write("DESW\t");
                    desw.ToString<string, double>().Write();
                    WeightedDirectedGraph.Edges.ToString<Edge>(format: "{0}\n", end: "", begin: "").Write();
                    //WeightedUndirectedGraph.Edges.ToString<Edge>(format: "{0}\n", end: "", begin: "").Write();
                }
#endif
            }
            throw new Exception($"Beyond safety loop count {count}. If this value is not enough. Please change this limit");

        }

        private List<(string net, string CT_CB, double ancw)> EdgeSelection(IReadOnlyCollection<string> unprocessedSet)
        {
            var result = new List<(string net, string CT_CB, double ancw)>();
            var ancw = new Dictionary<string, double>(CreateChainWeight.Ancestor(WeightedDirectedGraph).Where(b => unprocessedSet.Contains(b.Key)));
            var minAncw = ancw.Select(b => b.Value).Min();
            //unprocessed nodes with minimum ancw
            var CT = ancw.Where(a => a.Value == minAncw).Select(a => (a.Key, a.Value));
            var HT = new Graph();
            HT.AddVertexRange(CT.Select(a => a.Key));
            HT.AddEdgeRange(HorizontalConstrainGraph.Edges.Where(a => HT.Vertices.Contains(a.Source) && HT.Vertices.Contains(a.Target)));
            var desw = new Dictionary<string, double>(CreateChainWeight.Deanestor(WeightedDirectedGraph).Where(b => unprocessedSet.Contains(b.Key)));
            var minDesw = desw.Select(b => b.Value).Min();
            var CB = desw.Where(a => a.Value == minDesw).Select(b => (b.Key, b.Value));
            var HB = new Graph();
            HB.AddVertexRange(CB.Select(a => a.Key));
            HB.AddEdgeRange(HorizontalConstrainGraph.Edges.Where(a => HB.Vertices.Contains(a.Source) && HB.Vertices.Contains(a.Target)));
            if (HT.Edges.Count() < HB.Edges.Count())
            {
                int count = 0;
                //Do TOP
                while (CT.Count() != 0)
                {
                    if (count++ > 100) throw new Exception("CT止まらん");

                    List<(string Key, double Value)> PT
                        = CT.Select(a => (a.Key, Math.Max(ancw[a.Key] + desw[a.Key], LocalMaximumDensity[a.Key]))).ToList();
                    var PTLARGE = PT.Max(a => a.Value);

                    // rule 1 (5) if( there are nodes in PT with ancw(i) +desw(i) ==LARGE)
                    //ancw+deswがlocal max densityより大きいやつがある場合はそれをまずやる。
                    if (PT.Where(a => a.Value == PTLARGE).Count() != 0)
                    {
                        PT = PT.Where(a => a.Value == PTLARGE).ToList();
                    }
                    // rule 2 (6)  find larget local Maximum Density
                    var PTMAX = PT.Max(a => LocalMaximumDensity[a.Key]);
                    PT = PT.Where(a => LocalMaximumDensity[a.Key] == PTMAX).ToList();

                    // rule 3 (8) find largest desw
                    var PTDeswMax = PT.Select(a => a.Key).Max(b => desw[b]);
                    PT = PT.Where(a => desw[a.Key] == PTDeswMax).ToList();

                    var processPT = PT.Select(a => a.Key).ToList();
                    // processed it

                    ///DO PROCESSSSS
                    //these vertices are incoming direction
                    ProcessNodes(processPT, "outgoing");
                    var temp = unprocessedSet.Except(processPT).ToList();
                    //update CT
                    CT = CT.Where(a => temp.Contains(a.Key));
                    foreach (var item in processPT)
                    {
                        result.Add((item, "CT", ancw[item]));
                    }
                }
            }
            else
            {
                int count = 0;
                //Do BOT
                while (CB.Count() != 0)
                {
                    if (count++ > 100) throw new Exception("CB止まらん");
                    List<(string Key, double Value)> PB
                                            = CB.Select(a => (a.Key, Math.Max(ancw[a.Key] + desw[a.Key], LocalMaximumDensity[a.Key]))).ToList();
                    var PBLARGE = PB.Max(a => a.Value);
                    // rule 1 (13) find ancw+decw==PTLARGE

                    if (PB.Where(a => a.Value == PBLARGE).Count() != 0)
                    {
                        PB = PB.Where(a => a.Value == PBLARGE).ToList();
                    }
                    // rule 2 (14)  find larget local Maximum Density
                    var PBMAX = PB.Max(b => LocalMaximumDensity[b.Key]);
                    PB = PB.Where(a => LocalMaximumDensity[a.Key] == PBMAX).ToList();

                    // rule 3 (15) find largest decw
                    var PBAncwMax = PB.Select(a => a.Key).Max(b => ancw[b]);
                    PB = PB.Where(a => ancw[a.Key] == PBAncwMax).ToList();

                    var processPB = PB.Select(a => a.Key).ToList();
                    // processed it

                    ///DO PROCESSSSS
                    //these vertices are incoming direction
                    ProcessNodes(processPB, "incoming");
                    var temp = unprocessedSet.Except(processPB).ToList();
                    //update CT
                    CB = CB.Where(a => temp.Contains(a.Key));
                    foreach (var item in processPB)
                    {
                        result.Add((item, "CB", desw[item]));
                    }
                }
            }
            return result;
        }

        private (double Weight, string Source, string Target) CalcLabel(string Source, string Target, Dictionary<string, double> ancw, Dictionary<string, double> decw)
        {
            ///論文にはサイクルができないようにするって書いてあるけど、無向グラフがあったらどうやってもサイクルできちゃうので、
            ///(VCG+すでに向きを割り当てたEdgeで、)サイクルができないようにするってことでいいのか？？？？多分そう。

            //VCGとHCGの両方で辺がはられている場合は、VCGの向きで有効辺をはり、重さはHCGから取る。
            //DAGを維持するためには、トポロジカルソートができなかったらその逆向きに貼れば良い
            var tempGraph = new Graph();
            tempGraph.AddVertexRange(WeightedDirectedGraph.Vertices);
            tempGraph.AddEdgeRange(WeightedDirectedGraph.Edges);
            var guid = Guid.NewGuid();
            var EdgeIJ = WeightedUndirectedGraph.Edges.Where(a => a.Target == Target && a.Source == Source).First();
            //i->jが行けるか試す。
            var edge = new Edge(guid.ToString(), Source, Target, EdgeIJ.Weight);
            tempGraph.AddEdge(edge);
            try
            {
                tempGraph.TopologicalSort();
            }
            catch (NonAcyclicGraphException)
            {
                return (double.PositiveInfinity, Target, Source);
            }
            tempGraph.RemoveEdge(edge);
            //j->iが行けるか試す。
            edge = new Edge(guid.ToString(), Target, Source, EdgeIJ.Weight);
            tempGraph.AddEdge(edge);
            try
            {
                tempGraph.TopologicalSort();
            }
            catch (NonAcyclicGraphException)
            {
                return (double.PositiveInfinity, Source, Target);
            }
            tempGraph.RemoveEdge(edge);
            //どっちもできるので、実数を投げる。
            var left = ancw[Source] + decw[Target] + MinSeparation(Source, Target);
            var right = ancw[Target] + decw[Source] + MinSeparation(Source, Target);
            if (left == right)
            {
                return (Math.Max(left, right), "-1", "-1");
                //Node Selection行き
            }
            else if (left > right)
            {
                return (Math.Max(left, right), Source, Target);
            }
            else
            {
                return (Math.Max(left, right), Target, Source);
            }
        }


        private void AddEdgeWeightedDirectedGraph(Edge removeEdge, (double Label, string Source, string Target) addedge)
        {
            AddEdgeWeightedDirectedGraph(removeEdge, (addedge.Source, addedge.Target));
        }
        private void AddEdgeWeightedDirectedGraph(Edge removeEdge, (string Source, string Target) addedge)//いい名前がない
        {
            WeightedUndirectedGraph.RemoveEdge(removeEdge);
            WeightedDirectedGraph.TryGetEdge(addedge.Source, addedge.Target, out Edge edge);
            if (edge == null)//this is new edge
            {
                edge = new Edge($"net{addedge.Source}{addedge.Target}", addedge.Source, addedge.Target, removeEdge.Weight);
            }
            else
            {
                WeightedDirectedGraph.RemoveEdge(edge);
                edge.Weight = Math.Max(edge.Weight, removeEdge.Weight);
            }
            WeightedDirectedGraph.AddEdge(edge);
        }


        private double MinSeparation(string i, string j)
        {
            return Constant.minSpacing + wires[i].horizontal / 2 + wires[j].horizontal / 2;
        }

        private void ProcessNodes(IEnumerable<string> netName, string direction)
        {
            if (direction == "incoming")
            {
                foreach (var net in netName)
                {
                    foreach (var removeEdge in new ConcurrentBag<Edge>(weightedUndirectedGraph.Edges.Where(a => a.Target == net || a.Source == net)))
                    {
                        var source = removeEdge.Target == net ? removeEdge.Source : removeEdge.Target;
                        AddEdgeWeightedDirectedGraph(removeEdge, (source, net));
                    }
                }
            }
            else if (direction == "outgoing")
            {
                foreach (var net in netName)
                {
                    foreach (var removeEdge in new ConcurrentBag<Edge>(weightedUndirectedGraph.Edges.Where(a => a.Target == net || a.Source == net)))
                    {
                        var target = removeEdge.Source == net ? removeEdge.Target : removeEdge.Source;
                        AddEdgeWeightedDirectedGraph(removeEdge, (net, target));
                    }
                }
            }
            else
            {
                throw new ArgumentException("direction should be incoming/outgoing");
            }
        }
    }
}