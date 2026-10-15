// Keep repository inspection within the local model's 32K context.
// Pi's tool_call event supports mutating validated input before execution.
export default function (pi: any) {
    pi.on("tool_call", async (event: any) => {
        if (event.toolName === "read" && event.input && typeof event.input === "object") {
            event.input.limit = Math.min(event.input.limit ?? 160, 160);
        }
    });
}
