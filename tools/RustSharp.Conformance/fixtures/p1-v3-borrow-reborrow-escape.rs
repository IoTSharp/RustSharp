// frozen P1 fixture: borrow-reborrow-escape
fn main() {
    let mut value = 1;
    let parent = &mut value;
    {
        let child = &mut *parent;
        *child = 2;
    }
    *parent = 3;
    println!("{}", value);
}
