﻿using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Editor.NodeEditor;
using Facepunch.ActionGraphs;
using Sandbox.ActionGraphs;
using Sandbox.Internal;
using PropertyAttribute = Sandbox.PropertyAttribute;
using Connection = Editor.NodeEditor.Connection;

namespace Editor.ActionGraph;

public static class ActionGraphExtensions
{
	private static DisplayInfo ConstDisplayInfo( Node node )
	{
		var name = node.Properties.TryGetValue( "name", out var nameProperty )
			? nameProperty.Value as string
			: null;

		return new DisplayInfo
		{
			Name = string.IsNullOrEmpty( name ) ? node.DisplayInfo.Title : name,
			Description = node.DisplayInfo.Description,
			Icon = node.DisplayInfo.Icon,
			Tags = node.DisplayInfo.Tags
		};
	}

	public static DisplayInfo GetDisplayInfo( this Node node )
	{
		switch ( node.Definition.Identifier )
		{
			case "input":
				return new()
				{
					Name = node.ActionGraph.Title ?? node.DisplayInfo.Title,
					Description = node.DisplayInfo.Description,
					Icon = node.DisplayInfo.Icon,
					Tags = node.DisplayInfo.Tags
				};

			case { } s when s.StartsWith( "const." ):
				return ConstDisplayInfo( node );

			default:
				return new()
				{
					Name = node.DisplayInfo.Title,
					Description = node.DisplayInfo.Description,
					Icon = node.DisplayInfo.Icon,
					Tags = node.DisplayInfo.Tags
				};
		}
	}
}

[EditorForAssetType( "action" )]
public partial class MainWindow : DockWindow, IAssetEditor
{
	private static Dictionary<Guid, MainWindow> Instances { get; } = new Dictionary<Guid, MainWindow>();

	[StackTraceBuilder]
	public static void BuildStackTrace( Widget parent, NodeInvocationException e )
	{
		var row = parent.Layout.AddRow();
		row.Spacing = 8;
		row.Margin = 8;

		var stack = new List<NodeInvocationException>();
		var baseException = (Exception) e;

		while ( true )
		{
			stack.Add( e );

			if ( e.InnerException is NodeInvocationException inner )
			{
				e = inner;
			}
			else
			{
				baseException = e.InnerException ?? e;
				break;
			}
		}

		stack.Reverse();

		var message = row.Add( new Label( baseException.Message ), 1 );
		message.WordWrap = true;

		var button = new Button( "Copy To Clipboard" );
		button.Clicked = () =>
		{
			var message = baseException.Message;
			message += "\n";
			message += string.Join( "\n", stack.Select( x => x.Node.GetDisplayInfo().Name ) );
			EditorUtility.Clipboard.Copy( message );
		};

		row.Add( button );

		foreach ( var frame in stack )
		{
			AddStackLine( frame.Node, parent.Layout );
		}
	}

	private static void AddStackLine( Node node, Layout target )
	{
		if ( node == null )
			return;

		var row = new StackRow( node.GetDisplayInfo().Name, node.ActionGraph.Title );
		row.IsFromEngine = false;
		row.MouseClick += () =>
		{
			var window = Open( node.ActionGraph );
			var matchingNode = node.ActionGraph == window.ActionGraph
				? node
				: window.ActionGraph.Nodes.FirstOrDefault( x => x.Id == node.Id );

			window.View.SelectNode( matchingNode );
			window.View.CenterOnSelection();
		};

		target.Add( row );
	}

	public static MainWindow Open( Facepunch.ActionGraphs.ActionGraph actionGraph, string name = null )
	{
		if ( string.IsNullOrEmpty( actionGraph.Title ) )
		{
			actionGraph.Title = name;
		}

		var guid = actionGraph.GetGuid();

		if ( !Instances.TryGetValue( guid, out var inst ) )
		{
			Instances[guid] = inst = new MainWindow();
			inst.Init( actionGraph );
		}

		inst.Show();
		inst.Focus();
		return inst;
	}

	public Asset Asset { get; private set; }
	public ActionGraphResource Resource { get; private set; }
	public Facepunch.ActionGraphs.ActionGraph ActionGraph { get; private set; }

	public ActionGraph Graph { get; private set; }
	public ActionGraphView View { get; private set; }
	public Properties Properties { get; private set; }
	public ErrorList ErrorList { get; private set; }

	public event Action Saved;

	private readonly UndoStack _undoStack = new();
	private Option _undoMenuOption;
	private Option _redoMenuOption;

	public bool CanOpenMultipleAssets => false;

	public void AssetOpen( Asset asset )
	{
		var resource = asset.LoadResource<ActionGraphResource>();

		resource.Graph ??= Facepunch.ActionGraphs.ActionGraph.CreateEmpty( EditorNodeLibrary );

		Init( resource.Graph, asset, resource );
		Show();
	}

	public void Init( Facepunch.ActionGraphs.ActionGraph actionGraph, Asset asset = null, ActionGraphResource resource = null )
	{
		if ( ActionGraph != null )
		{
			ActionGraphDebugger.StopListening( ActionGraph );
		}
		else
		{
			ResourceSaved += OnResourceSaved;
		}

		DeleteOnClose = true;

		Asset = asset;
		Resource = resource;
		ActionGraph = actionGraph;
		Graph = new ActionGraph( actionGraph );
		
		Size = new Vector2( 1280, 720 );

		UpdateTitle();
		RebuildUI();

		try
		{
			ActionGraphDebugger.StartListening( actionGraph, OnLinkTriggered );
		}
		catch ( Exception e )
		{
			Log.Error( e );
		}
	}

	private void UpdateTitle()
	{
		Title = $"{ActionGraph.Title} - Action Graph";
	}

	private void OnResourceSaved( ActionGraphResource resource )
	{
		var path = resource.ResourcePath;

		var matchingNodes = ActionGraph.Nodes
			.Where( x => x.Definition.Identifier == "graph" )
			.Where( x =>
				x.Properties["graph"].Value is string refPath &&
				refPath.Equals( path, StringComparison.OrdinalIgnoreCase ) )
			.ToArray();

		Log.Info( $"{matchingNodes.Length} matches with path \"{path}\" in {Asset?.Path}!" );

		foreach ( var node in matchingNodes )
		{
			// Force referencing nodes to invalidate

			node.Properties["graph"].Value = null;
			node.Properties["graph"].Value = path;
		}
	}

	private void OnLinkTriggered( Link link, object value )
	{
		View.LinkTriggered( link, value );
	}

	[EditorEvent.Frame]
	public void Frame()
	{
		var updated = Graph.Update();

		if ( updated.Any() )
		{
			View.UpdateConnections( updated );

			foreach ( var item in View.Items )
			{
				item.Update();
			}
		}

		_undoMenuOption.Enabled = _undoStack.CanUndo;
		_redoMenuOption.Enabled = _undoStack.CanRedo;
		_undoMenuOption.Text = _undoStack.UndoName ?? "Undo";
		_redoMenuOption.Text = _undoStack.RedoName ?? "Redo";
		_undoMenuOption.StatusText = _undoStack.UndoName;
		_redoMenuOption.StatusText = _undoStack.RedoName;
	}

	[Event.Hotload]
	public void RebuildUI()
	{
		View = new ActionGraphView( null )
		{
			Graph = Graph,
			WindowTitle = "Graph View"
		};
		View.SetBackgroundImage( "toolimages:/grapheditor/grapheditorbackgroundpattern_shader.png" );
		View.OnSelectionChanged += View_OnSelectionChanged;

		foreach ( var nodeDefinition in EditorNodeLibrary.All.Values )
		{
			View.AddNodeType( new ActionNodeType( nodeDefinition ) );
		}

		Properties = new Properties( null )
		{
			Target = Graph
		};

		ErrorList = new ErrorList( null, this );

		DockManager.Clear();
		DockManager.RegisterDockType( "Properties", "edit", () => new Properties(null) { Target = Graph }, false );
		DockManager.RegisterDockType( "ErrorList", "error", () => new ErrorList( null, this ), false );

		DockManager.AddDock( null, View, DockArea.Right, properties: DockManager.DockProperty.HideCloseButton | DockManager.DockProperty.HideOnClose );
		DockManager.AddDock( null, Properties, DockArea.Left, DockManager.DockProperty.HideOnClose, split: 0.33f );
		DockManager.AddDock( Properties, ErrorList, DockArea.Bottom, DockManager.DockProperty.HideOnClose, split: 0.75f );
		DockManager.Update();

		MenuBar.Clear();

		{
			var file = MenuBar.AddMenu( "File" );
			file.AddOption( new Option( "Save" ) { Shortcut = "Ctrl+S", Triggered = Save } );
			file.AddSeparator();
			file.AddOption( new Option( "Exit" ) { Triggered = Close } );
		}

		{
			var edit = MenuBar.AddMenu( "Edit" );
			_undoMenuOption = edit.AddOption( "Undo", "undo", Undo, "Ctrl+Z" );
			_redoMenuOption = edit.AddOption( "Redo", "redo", Redo, "Ctrl+Y" );
			_undoMenuOption.Enabled = _undoStack.CanUndo;
			_redoMenuOption.Enabled = _undoStack.CanRedo;

			edit.AddSeparator();
			edit.AddOption( "Cut", "common/cut.png", CutSelection, "Ctrl+X" );
			edit.AddOption( "Copy", "common/copy.png", CopySelection, "Ctrl+C" );
			edit.AddOption( "Paste", "common/paste.png", PasteSelection, "Ctrl+V" );
			edit.AddOption( "Select All", "select_all", SelectAll, "Ctrl+A" );
		}
	}

	private void View_OnSelectionChanged()
	{
		var node = View.SelectedItems
			.OfType<NodeUI>()
			.MaxBy( n => n is CommentUI );

		if ( node != null )
		{
			Properties.Target = node.Node;
		}
		else
		{
			Properties.Target = Graph;
		}
	}

	private static Action<ActionGraphResource> ResourceSaved;

	private void Save()
	{
		Saved?.Invoke();

		if ( Asset != null )
		{
			Resource.Graph = ActionGraph;

			Asset.SaveToMemory( Resource );
			Asset.SaveToDisk( Resource );

			ResourceSaved?.Invoke( Resource );
		}

		UpdateTitle();
	}

	private void Undo()
	{

	}

	private void Redo()
	{

	}

	private void CutSelection()
	{
		View.CutSelection();
	}

	private void CopySelection()
	{
		View.CopySelection();
	}

	private void PasteSelection()
	{
		View.PasteSelection();
	}

	private void SelectAll()
	{
		View.SelectAll();
	}

	protected override bool OnClose()
	{
		ActionGraphDebugger.StopListening( ActionGraph );

		ResourceSaved -= OnResourceSaved;

		var guid = ActionGraph.GetGuid();

		if ( Instances.TryGetValue( guid, out var inst ) && inst == this )
		{
			Instances.Remove( guid );
		}

		return base.OnClose();
	}
}

public class ActionGraphView : GraphView
{
	private class Pulse
	{
		public object Value { get; set; }
		public float Time { get; set; }
	}

	private Dictionary<Connection, Pulse> Pulses { get; } = new();
	private List<Connection> FinishedPulsing { get; } = new List<Connection>();

	public new ActionGraph Graph
	{
		get => (ActionGraph)base.Graph;
		set => base.Graph = value;
	}

	public ActionGraphView( Widget parent ) : base( parent )
	{

	}

	protected override INodeType RerouteNodeType { get; }
		= new ActionNodeType( EditorNodeLibrary.Get( "nop" ) );

	protected override INodeType CommentNodeType { get; }
		= new ActionNodeType( EditorNodeLibrary.Get( "comment" ) );

	public void LinkTriggered( Link link, object value )
	{
		var connection = Items.OfType<Connection>()
			.FirstOrDefault( x => x.Input.Inner is ActionPlug<Node.Input, InputDefinition> plugIn && plugIn.Parameter == link.Target
				&& x.Output.Inner is ActionPlug<Node.Output, OutputDefinition> plugOut && plugOut.Parameter == link.Source );

		if ( connection == null )
		{
			return;
		}

		Pulses[connection] = new Pulse { Time = 1f, Value = value };
	}

	[EditorEvent.Frame]
	public void Frame()
	{
		var dt = Time.Delta;

		FinishedPulsing.Clear();

		foreach ( var pulse in Pulses )
		{
			if ( !pulse.Key.IsValid || pulse.Value.Time < 0f )
			{
				FinishedPulsing.Add( pulse.Key );
			}
			else
			{
				pulse.Value.Time -= dt;
				pulse.Key.WidthScale = 1f + MathF.Pow( Math.Max( pulse.Value.Time, 0f ), 8f ) * 3f;
				pulse.Key.Update();
			}
		}

		foreach ( var connection in FinishedPulsing )
		{
			Pulses.Remove( connection );
		}
	}

	public void SelectNode( Node node )
	{
		var actionNode = Graph.FindNode( node );

		SelectNode( actionNode );
	}

	public void SelectLink( Link link )
	{
		SelectLinks( new [] { link } );
	}

	public void SelectLinks( IEnumerable<Link> links )
	{
		var linkSet = links.Select( x => (x.Source, x.Target) ).ToHashSet();

		var connections = Items.OfType<Connection>().Where( x =>
			x.Input.Inner is ActionPlug<Node.Input, InputDefinition> { Parameter: { } input } &&
			x.Output.Inner is ActionPlug<Node.Output, OutputDefinition> { Parameter: { } output } &&
			linkSet.Contains( (output, input) ) );

		foreach ( var item in SelectedItems )
		{
			item.Selected = false;
		}

		foreach ( var connection in connections )
		{
			connection.Selected = true;
		}
	}

	private static bool IsMemberPublic( MemberDescription memberDesc )
	{
		if ( memberDesc.IsPublic )
		{
			return true;
		}

		return memberDesc.GetCustomAttribute<PropertyAttribute>() is { };
	}

	private static int MemberTypeOrdinal( MemberDescription memberDesc )
	{
		switch ( memberDesc )
		{
			case FieldDescription:
			case PropertyDescription:
				return 0;

			default:
				return 1;
		}
	}

	private static IEnumerable<INodeType> GetInstanceNodes( Type type )
	{
		if ( type == null )
		{
			yield break;
		}

		if ( type.BaseType != null )
		{
			foreach ( var node in GetInstanceNodes( type.BaseType ) )
			{
				yield return node;
			}
		}

		var typeDesc = EditorTypeLibrary.GetType( type );

		if ( typeDesc == null )
		{
			yield break;
		}

		var methods = new List<MethodDescription>();

		foreach ( var memberDesc in typeDesc.DeclaredMembers.OrderBy( MemberTypeOrdinal ).ThenBy( x => x.Name ) )
		{
			if ( !IsMemberPublic( memberDesc ) || memberDesc.IsStatic || memberDesc.IsActionGraphIgnored() )
			{
				continue;
			}

			switch ( memberDesc )
			{
				case MethodDescription methodDesc:
					if ( methodDesc.IsSpecialName )
					{
						break;
					}

					if ( !methodDesc.AreParametersActionGraphSafe() )
					{
						break;
					}

					methods.Add( methodDesc );
					break;

				case PropertyDescription propertyDesc:
					{
						if ( propertyDesc.IsIndexer )
						{
							// TODO
							break;
						}

						var canRead = propertyDesc.IsGetMethodPublic || propertyDesc.CanRead && propertyDesc.HasAttribute<PropertyAttribute>();
						var canWrite = propertyDesc.IsSetMethodPublic || propertyDesc.CanWrite && propertyDesc.HasAttribute<PropertyAttribute>();

						if ( canRead )
						{
							yield return new PropertyNodeType( propertyDesc, PropertyNodeKind.Get, canWrite );
						}

						if ( canWrite )
						{
							yield return new PropertyNodeType( propertyDesc, PropertyNodeKind.Set, canRead );
						}

						break;
					}

				case FieldDescription fieldDesc:
					yield return new FieldNodeType( fieldDesc, PropertyNodeKind.Get, !fieldDesc.IsInitOnly );

					if ( !fieldDesc.IsInitOnly )
					{
						yield return new FieldNodeType( fieldDesc, PropertyNodeKind.Set, true );
					}

					break;
			}
		}

		foreach ( var methodGroup in methods.GroupBy( x => (x.Name, x.IsStatic) ) )
		{
			yield return new MethodNodeType( methodGroup.ToArray() );
		}
	}

	protected override IEnumerable<INodeType> GetRelevantNodes( string name )
	{
		foreach ( var variable in Graph.Graph.Variables.OrderBy( x => x.Name ) )
		{
			yield return new VariableNodeType( variable.Name, variable.Type, PropertyNodeKind.Get, false, true );
			yield return new VariableNodeType( variable.Name, variable.Type, PropertyNodeKind.Set, false, true );
		}

		foreach ( var resource in GlobalGameNamespace.ResourceLibrary.GetAll<ActionGraphResource>() )
		{
			if ( resource.Graph == null )
			{
				continue;
			}

			yield return new GraphNodeType( resource );
		}

		foreach ( var nodeType in base.GetRelevantNodes( name ) )
		{
			yield return nodeType;
		}
	}

	protected override IEnumerable<INodeType> GetRelevantNodes( Type inputValueType, string name )
	{
		name = name?.Trim();

		if ( inputValueType != typeof(OutputSignal) && !string.IsNullOrEmpty( name ) )
		{
			var match = Graph.Graph.Variables.FirstOrDefault( x => x.Name == name );

			if ( match == null )
			{
				yield return new VariableNodeType( name, inputValueType, PropertyNodeKind.Set, true, false );
			}
		}

		foreach ( var variable in Graph.Graph.Variables.OrderBy( x => x.Name ) )
		{
			if ( !variable.Type.IsAssignableFrom( inputValueType ) )
			{
				continue;
			}

			yield return new VariableNodeType( variable.Name, variable.Type, PropertyNodeKind.Set, false, false );
		}

		foreach ( var nodeType in GetInstanceNodes( inputValueType ) )
		{
			yield return nodeType;
		}

		foreach ( var nodeType in base.GetRelevantNodes( inputValueType, name ) )
		{
			yield return nodeType;
		}
	}

	private void RemoveInvalidElements()
	{
		var invalidNodes = Items
			.OfType<NodeUI>()
			.Where( x => x.Node is ActionNode { Node.IsValid: false } )
			.ToArray();

		var invalidConnections = Connections
			.Where( x =>
				x.Input.Node.Node is ActionNode { Node.IsValid: false } ||
				x.Output.Node.Node is ActionNode { Node.IsValid: false } )
			.ToArray();

		foreach ( var invalidNode in invalidNodes )
		{
			Graph.RemoveNode( (ActionNode) invalidNode.Node );
			invalidNode.Destroy();
		}

		foreach ( var connection in invalidConnections )
		{
			Connections.Remove( connection );
			connection.Destroy();
		}
	}

	private void CleanUpNewSubGraph( Facepunch.ActionGraphs.ActionGraph graph )
	{
		const string positionKey = "Position";
		const float inputOutputMargin = 300f;

		var minPos = new Vector2( float.PositiveInfinity, float.PositiveInfinity );
		var maxPos = new Vector2( float.NegativeInfinity, float.NegativeInfinity );
		var posCount = 0;

		foreach ( var node in graph.Nodes )
		{
			if ( node.UserData[positionKey] is not { } posValue )
			{
				continue;
			}

			var pos = posValue.Deserialize<Vector2>();

			minPos = Vector2.Min( minPos, pos );
			maxPos = Vector2.Max( maxPos, pos );
			posCount++;
		}

		if ( posCount == 0 )
		{
			minPos = maxPos = 0f;
		}

		var midPos = (minPos + maxPos) * 0.5f;
		var width = maxPos.x - minPos.x;

		foreach ( var node in graph.Nodes )
		{
			if ( node.UserData[positionKey] is not { } posValue )
			{
				continue;
			}

			var pos = posValue.Deserialize<Vector2>() - midPos;
			node.UserData[positionKey] = JsonSerializer.SerializeToNode( pos );
		}

		if ( graph.InputNode is { } input )
		{
			var pos = new Vector2( width * -0.5f - inputOutputMargin, 0f );
			input.UserData[positionKey] = JsonSerializer.SerializeToNode( pos );
		}

		if ( graph.PrimaryOutputNode is { } output )
		{
			var pos = new Vector2( width * 0.5f + inputOutputMargin, 0f );
			output.UserData[positionKey] = JsonSerializer.SerializeToNode( pos );
		}
	}

	protected override void OnOpenContextMenu( Menu menu, PlugOut nodeOutput )
	{
		var selectedNodes = SelectedItems
			.OfType<NodeUI>()
			.Select( x => (NodeUI: x, ActionNode: x.Node is ActionNode { Node: { } node } ? node : null) )
			.Where( x => x.ActionNode != null )
			.ToArray();

		var actionGraph = Graph.Graph;

		if ( actionGraph.CanCreateSubGraph( selectedNodes.Select( x => x.ActionNode ) ) )
		{
			menu.AddOption( "Create Custom Node...", "add_box", async () =>
			{
				var avgPos = selectedNodes
					.Aggregate( Vector2.Zero, ( s, x ) => s + x.NodeUI.Position )
					/ selectedNodes.Length;

				var result = await actionGraph.CreateSubGraphAsync( selectedNodes.Select( x => x.ActionNode ), async subGraph =>
				{
					const string extension = "action";

					var fd = new FileDialog( null );
					fd.Title = "Create ActionGraph Node";
					fd.Directory = Path.GetDirectoryName( LocalProject.CurrentGame.Path );
					fd.DefaultSuffix = $".{extension}";
					fd.SelectFile( $"untitled.{extension}" );
					fd.SetFindFile();
					fd.SetModeSave();
					fd.SetNameFilter( $"ActionGraph Node (*.{extension})" );

					if ( !fd.Execute() )
						return null;

					var fileName = Path.GetFileNameWithoutExtension( fd.SelectedFile );
					var title = fileName.ToTitleCase();

					var asset = AssetSystem.CreateResource( "action", fd.SelectedFile );
					var resource = asset.LoadResource<ActionGraphResource>();

					CleanUpNewSubGraph( subGraph );

					resource.Graph = subGraph;
					resource.Title = title;
					resource.Description = "No description provided.";
					resource.Icon = "account_tree";
					resource.Category = "Custom";

					asset.SaveToMemory( resource );
					asset.SaveToDisk( resource );

					MainAssetBrowser.Instance?.UpdateAssetList();

					return asset.Path;
				} );

				var newNode = new ActionNode( Graph, result!.Value.GraphNode );

				Graph.AddNode( newNode );

				RemoveInvalidElements();
				BuildFromNodes( new[] { newNode }, avgPos, true );
			} );
		}
	}

	private static Dictionary<Type, HandleConfig> HandleConfigs { get; } = new()
	{
		{ typeof(OutputSignal), new HandleConfig( "Signal", Color.White, HandleShape.Arrow ) },
		{ typeof(Task), new HandleConfig( "Signal", Color.White, HandleShape.Arrow ) },
		{ typeof(GameObject), new HandleConfig( null, Theme.Blue ) },
		{ typeof(BaseComponent), new HandleConfig( null, Theme.Green ) },
		{ typeof(float), new HandleConfig( "Float", Color.Parse( "#8ec07c" )!.Value ) },
		{ typeof(int), new HandleConfig( "Int", Color.Parse( "#ce67e0" )!.Value ) },
		{ typeof(bool), new HandleConfig( "Bool", Color.Parse( "#e0d867" )!.Value ) },
		{ typeof(Vector3), new HandleConfig( "Vector3", Color.Parse( "#7177e1" )!.Value ) },
		{ typeof(string), new HandleConfig( "String", Color.Parse( "#c7ae32" )!.Value ) }
	};

	protected override HandleConfig OnGetHandleConfig( Type type )
	{
		if ( HandleConfigs.TryGetValue( type, out var config ) )
		{
			return config;
		}

		if ( type.BaseType != null )
		{
			return OnGetHandleConfig( type.BaseType );
		}

		return base.OnGetHandleConfig( type );
	}
}
