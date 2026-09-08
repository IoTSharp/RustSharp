mod api { fn f() {} }
use crate::api::f as _;
fn local() {}
fn main() { local(); }
