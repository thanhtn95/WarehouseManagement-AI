import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
  DialogFooter,
  Button,
} from 'web';

// Rendered forced-open (defaultOpen) since this is a static preview — the
// closed state is just the trigger, which is already shown by the Button
// preview.
export function Default() {
  return (
    <Dialog defaultOpen>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Suspend this user?</DialogTitle>
          <DialogDescription>
            Ada Receiver will be signed out on their next request. 7 unsynced
            device queue entries will be stranded.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter showCloseButton>
          <Button variant="destructive">Suspend</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
