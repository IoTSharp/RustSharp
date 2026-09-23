use ::crate_name::{self as whole, nested::{Thing as Alias, *}, Other as _};
pub(in crate::inner) mod external;
pub mod inline {}
/// docs
fn f() {}
