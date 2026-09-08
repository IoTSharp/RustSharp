mod api { pub(crate) fn f() {} }
pub use crate::api::{f};
fn local() {}
fn main() { local(); }
