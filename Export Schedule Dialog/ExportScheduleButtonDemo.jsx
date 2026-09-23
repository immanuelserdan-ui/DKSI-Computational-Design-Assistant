import { useState } from "react";
import ExportScheduleDialog from "./ExportScheduleDialog";

/** Example usage: a toolbar button that opens the export dialog. */
export default function ExportScheduleButtonDemo() {
  const [isDialogOpen, setIsDialogOpen] = useState(false);

  return (
    <>
      <button
        type="button"
        onClick={() => setIsDialogOpen(true)}
        className="inline-flex items-center gap-2 rounded-md border border-gray-300 px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
      >
        Export Schedules
      </button>

      <ExportScheduleDialog
        isOpen={isDialogOpen}
        onClose={() => setIsDialogOpen(false)}
      />
    </>
  );
}
