import { useEffect, useRef, useState } from "react";
import { exportScheduleToExcel } from "./exportScheduleService";

const DEFAULT_CATEGORIES = [
  "All Schedules",
  "Door Schedule",
  "Window Schedule",
  "Room Schedule",
  "Wall Schedule",
  "Furniture Schedule",
  "Sheet List",
];

const STATUS_OPTIONS = ["Active", "Pending", "Completed"];

/**
 * Modal dialog for filtering and exporting schedules to Excel.
 *
 * @param {boolean} isOpen
 * @param {() => void} onClose
 * @param {string[]} [categories] - dropdown options, defaults to DEFAULT_CATEGORIES
 * @param {(filters: object) => Promise<any>} [onExport] - defaults to the mock service
 */
export default function ExportScheduleDialog({
  isOpen,
  onClose,
  categories = DEFAULT_CATEGORIES,
  onExport = exportScheduleToExcel,
}) {
  const [startDate, setStartDate] = useState("");
  const [endDate, setEndDate] = useState("");
  const [category, setCategory] = useState(categories[0]);
  const [statuses, setStatuses] = useState({
    Active: true,
    Pending: true,
    Completed: false,
  });
  const [isExporting, setIsExporting] = useState(false);
  const [error, setError] = useState(null);

  const dialogRef = useRef(null);

  // Reset transient state each time the dialog is opened.
  useEffect(() => {
    if (isOpen) {
      setError(null);
      setIsExporting(false);
      dialogRef.current?.focus();
    }
  }, [isOpen]);

  useEffect(() => {
    function handleKeyDown(event) {
      if (event.key === "Escape" && !isExporting) onClose();
    }
    if (isOpen) document.addEventListener("keydown", handleKeyDown);
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, [isOpen, isExporting, onClose]);

  if (!isOpen) return null;

  const toggleStatus = (status) =>
    setStatuses((prev) => ({ ...prev, [status]: !prev[status] }));

  const dateRangeInvalid =
    startDate && endDate && startDate > endDate;

  const handleExport = async () => {
    if (dateRangeInvalid) {
      setError("Start date must be before end date.");
      return;
    }

    const selectedStatuses = STATUS_OPTIONS.filter((s) => statuses[s]);
    if (selectedStatuses.length === 0) {
      setError("Select at least one status.");
      return;
    }

    setError(null);
    setIsExporting(true);
    try {
      await onExport({
        startDate: startDate || null,
        endDate: endDate || null,
        category,
        statuses: selectedStatuses,
      });
      onClose();
    } catch (err) {
      setError(err?.message ?? "Export failed. Please try again.");
    } finally {
      setIsExporting(false);
    }
  };

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 px-4"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget && !isExporting) onClose();
      }}
    >
      <div
        ref={dialogRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="export-schedule-title"
        tabIndex={-1}
        className="w-full max-w-md rounded-lg bg-white p-6 shadow-xl outline-none"
      >
        <div className="mb-4 flex items-start justify-between">
          <div>
            <h2
              id="export-schedule-title"
              className="text-lg font-semibold text-gray-900"
            >
              Export Schedule
            </h2>
            <p className="mt-1 text-sm text-gray-500">
              Filter the schedules to include, then export to Excel.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            disabled={isExporting}
            aria-label="Close"
            className="text-gray-400 hover:text-gray-600 disabled:opacity-40"
          >
            ✕
          </button>
        </div>

        <div className="space-y-4">
          {/* Date range */}
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label
                htmlFor="start-date"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                Start Date
              </label>
              <input
                id="start-date"
                type="date"
                value={startDate}
                onChange={(e) => setStartDate(e.target.value)}
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>
            <div>
              <label
                htmlFor="end-date"
                className="mb-1 block text-sm font-medium text-gray-700"
              >
                End Date
              </label>
              <input
                id="end-date"
                type="date"
                value={endDate}
                onChange={(e) => setEndDate(e.target.value)}
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              />
            </div>
          </div>

          {/* Category / schedule name */}
          <div>
            <label
              htmlFor="category"
              className="mb-1 block text-sm font-medium text-gray-700"
            >
              Category / Schedule
            </label>
            <select
              id="category"
              value={category}
              onChange={(e) => setCategory(e.target.value)}
              className="w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
            >
              {categories.map((c) => (
                <option key={c} value={c}>
                  {c}
                </option>
              ))}
            </select>
          </div>

          {/* Status checkboxes */}
          <fieldset>
            <legend className="mb-1 text-sm font-medium text-gray-700">
              Status
            </legend>
            <div className="flex flex-wrap gap-4">
              {STATUS_OPTIONS.map((status) => (
                <label
                  key={status}
                  className="flex items-center gap-2 text-sm text-gray-700"
                >
                  <input
                    type="checkbox"
                    checked={statuses[status]}
                    onChange={() => toggleStatus(status)}
                    className="h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
                  />
                  {status}
                </label>
              ))}
            </div>
          </fieldset>

          {error && (
            <p className="text-sm text-red-600" role="alert">
              {error}
            </p>
          )}
        </div>

        <div className="mt-6 flex justify-end gap-3">
          <button
            type="button"
            onClick={onClose}
            disabled={isExporting}
            className="rounded-md border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            type="button"
            onClick={handleExport}
            disabled={isExporting}
            className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {isExporting && (
              <span className="h-3.5 w-3.5 animate-spin rounded-full border-2 border-white border-t-transparent" />
            )}
            {isExporting ? "Exporting..." : "Export to Excel"}
          </button>
        </div>
      </div>
    </div>
  );
}
