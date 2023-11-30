﻿namespace Sandbox;

public class SceneAnimationHook : SceneHook
{
	public SceneAnimationHook( Scene scene ) : base( scene )
	{
		Listen( Stage.UpdateBones, 0, UpdateAnimation, "UpdateAnimation" );
	}

	void UpdateAnimation()
	{
		if ( !Scene.ThreadedAnimation )
			return;

		// TODO - faster way to accumulate these
		var animModel = Scene.Components.GetAll<SkinnedModelRenderer>( FindMode.EnabledInSelfAndDescendants )
			.ToArray();

		//
		// Run the updates and the bone merges in a thread
		//
		Sandbox.Utility.Parallel.ForEach( animModel, x => UpdateInThread( x) );

		//
		// Run events in the main thread
		//
		using ( Sandbox.Utility.Superluminal.Scope( "Scene.AnimPostUpdate", Color.Yellow ) )
		{
			foreach ( var x in animModel )
			{
				x.PostAnimationUpdate();
			}
		}
	}

	public void UpdateInThread( SkinnedModelRenderer renderer )
	{
		// Skip out if we have a parent that is a skinned model, because we need to move relative to that
		// and their bones haven't been worked out yet. We'll get worked out after our parent is.
		if ( renderer.Components.GetInAncestors<SkinnedModelRenderer>() is not null )
			return;

		// Update in order
		foreach ( var c in renderer.Components.GetAll<SkinnedModelRenderer>( FindMode.EnabledInSelfAndDescendants ) )
		{
			c.AnimationUpdate();
		}
	}
}
