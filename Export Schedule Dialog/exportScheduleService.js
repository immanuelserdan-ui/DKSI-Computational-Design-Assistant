/**
 * Mock export handler. Swap the body of this function for a real API call
 * (e.g. POST the filters to a backend endpoint that streams back an .xlsx
 * file, or invoke a Revit/Dynamo automation hook) without touching the
 * dialog component.
 */
export async function exportScheduleToExcel(filters) {
  console.log("Exporting schedule with filters:", filters);

  await new Promise((resolve) => setTimeout(resolve, 1200));

  return {
    success: true,
    fileName: `Schedule_Export_${Date.now()}.xlsx`,
  };
}
