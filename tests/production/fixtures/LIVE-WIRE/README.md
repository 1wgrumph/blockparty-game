# LIVE-WIRE documentation fixtures

Retrieved 2026-10-30 through public documentation only. These are documentation
examples or explicitly parameterized schema instances, never live receipts.

- `image-png.json` — doc-sourced: https://dev.meta.ai/docs/image-generation#response-shape and https://dev.meta.ai/docs/api-reference/images/schemas . The example's elided WebP bytes are replaced with the suite's existing complete PNG, and output_format is png, as the actual ImageAdapter request specifies. Usage omitted (optional); this does not test a default WebP request.
- `voice-session.json` — doc-sourced: https://dev.meta.ai/docs/api-reference/voice/schemas#realtime-handshake-response . Schema instance; doc-session is a local correlation value.
- `voice-final.json` — doc-sourced: https://dev.meta.ai/docs/api-reference/voice/schemas#transcript-event . Schema instance; pause and audioProcessedMs are local test values, with every required field present.
- `voice-complete.json` — doc-sourced: https://dev.meta.ai/docs/api-reference/voice/schemas#speech-complete-event . Schema instance; pause, turnId and audioProcessedMs are local test values.
- `sam-nonempty.json` — doc-sourced: https://dev.meta.ai/docs/sam/reading-segmentation#video-output-grammar . The full two-object frame-0 line, including both complete mask payloads, is copied exactly. The same page's Image output grammar explicitly uses the same grammar for a still image. The non-streaming Responses envelope follows https://dev.meta.ai/docs/protocols/responses#example-response and https://dev.meta.ai/docs/sam/overview (non-streaming supported). Required envelope fields follow https://dev.meta.ai/docs/api-reference/responses/schemas#response, including error=null; IDs, metadata, timestamp and model are schema parameters, not claimed provider receipts. No ellipses or fabricated mask bytes.
- `sam-empty-null-error.json` — doc-sourced: https://dev.meta.ai/docs/api-reference/responses/schemas#response and https://dev.meta.ai/docs/sam/overview . The same complete response schema with the documented valid empty no-match lane isolates the required nullable error field from nonempty segmentation.
- `bad-request.json` — doc-sourced: https://dev.meta.ai/docs/error-handling#error-response-format . Exact published validation-error example, used with HTTP 400. It is not the unknown error body from the live d1 sitting.

No Meta d1-ultra success or request fixture is provided: public Meta docs checked
do not define that model or a named-question decision contract. Absence is a
coverage gap, not permission to relabel Muse Spark as d1-ultra.

Review-repair 2 additions (2026-10-30):

- `sam-payload-tilde.json` — grammar instance from `sam-nonempty.json`: replace
  the fourth payload `!` of the first mask with `~`, leaving its separate `~`
  encoding marker, numeric fields, second object and response envelope intact.
  https://dev.meta.ai/docs/sam/reading-segmentation#mask explicitly says PAYLOAD
  may contain `~` or `!` (retrieved 2026-10-30). This tests grammar acceptance
  through CaptureSearch's box-outline consumer, not valid raster decoding or
  a publisher-produced bitmap. It is not a live receipt.

- `voice-first-error.json` — schema instance of
  https://dev.meta.ai/docs/api-reference/voice/schemas#error-event (retrieved
  2026-10-30): required type/message/sessionId plus optional errorType/errorCode.
  Values are harmless local parameters, not a live receipt. Credential variants
  replace both identifiers with the suite's synthetic canary at runtime.
- A1 timing inputs (10, 11.3 and 30 real seconds) come from the owner amendment
  in docs/production/service-access-decision.json; 11.3 rounds the measured
  11287 ms HTTP 200 observation. No wall-clock wait or HTTP request is used.
- A2 expectations come from that same owner record: OD-4 stays open, d1-ultra
  belongs to Liquid AI and is deferred-local-fallback with zero sends.
