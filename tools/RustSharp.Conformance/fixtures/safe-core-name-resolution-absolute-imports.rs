mod api { pub fn f() {} }
use ::crate::api::f;
use ::crate::api::*;
fn local() {}
fn main() { local(); }
