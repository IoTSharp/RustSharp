mod api {
    pub type Shared = i32;
    pub fn Shared() {}
}
use crate::api::Shared as Both;
fn main(value: Both) -> Both { Both(); value }
