/** Retain the existing helper name while moving controls to opaque surfaces. */
export function glass(className = "") {
  return `surface ${className}`.trim();
}
