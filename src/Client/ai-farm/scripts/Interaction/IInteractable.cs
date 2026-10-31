namespace AIFarm.Interaction;

/// <summary>
/// Something the player can use with the action button (E / controller A) while aiming at it.
/// Implemented by the physics body the player's interact ray hits.
/// </summary>
public interface IInteractable
{
	void Interact();
}
