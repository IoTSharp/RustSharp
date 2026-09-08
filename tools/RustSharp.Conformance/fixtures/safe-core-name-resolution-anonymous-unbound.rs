mod api { pub fn f() {} }
use crate::api::{f as _, f as _};
use crate::api::f as _;
fn main() { f(); }
